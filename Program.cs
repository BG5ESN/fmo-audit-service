using EmqxMonitor;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

// ---- CLI 命令：--check 检查更新 / --update 执行更新（手动升级，替换后提示重启服务）----
if (args.Contains("--check") || args.Contains("--update"))
{
    if (args.Contains("--update"))
    {
        var (err, msg, replaced) = await UpdateService.ApplyAsync();
        if (err != null) { Console.WriteLine($"更新失败: {err}"); Environment.Exit(1); return; }
        Console.WriteLine(msg);
        if (replaced)
        {
            // 延迟替换脚本会在本进程退出后覆盖二进制，届时再提示重启
            if (OperatingSystem.IsWindows())
                Console.WriteLine("本进程退出后自动替换，完成后请执行: Start-ScheduledTask fmo-fas");
            else
                Console.WriteLine("本进程退出后自动替换。若以 systemd 服务运行: systemctl restart fmo-fas");
        }
        Environment.Exit(0);
        return;
    }
    var (cur, latest, has, cerr) = await UpdateService.CheckAsync();
    Console.WriteLine($"当前版本: {cur}");
    if (cerr != null) { Console.WriteLine(cerr); Environment.Exit(1); return; }
    Console.WriteLine(has ? $"发现新版本 v{latest}！执行 fmo-audit-service --update 更新" : $"已是最新版本 v{latest}");
    Environment.Exit(0);
    return;
}

// ---- 配置：端口（环境变量优先，默认 9527）----
var port = int.TryParse(Environment.GetEnvironmentVariable("EMQX_MONITOR_PORT"), out var envPort) ? envPort : 9527;

// ---- 数据库路径（环境变量可指定，默认用户数据目录）----
var dbPath = Environment.GetEnvironmentVariable("EMQX_MONITOR_DB");
if (string.IsNullOrEmpty(dbPath))
{
    var dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmqxMonitor");
    Directory.CreateDirectory(dataDir);
    dbPath = Path.Combine(dataDir, "emqx-monitor-server.db");
}
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

// 单文件模式下 wwwroot 内嵌于 exe，ContentRoot 指向自解压目录，否则静态文件 404
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
// 全局请求体上限 1MB：所有 POST 端点 body 都很小，防 ingest webhook 超大 body 内存炸弹（Kestrel 默认 30MB）
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
    o.Limits.MaxRequestBodySize = 1_048_576);

var db = new Database(dbPath);
var emqx = new EmqxClient();
var auth = new AuthService(db);
var health = new HostHealthCollector();
var collector = new CollectorService(emqx, db, health);
var topicIngest = new TopicIngestService(db);
// 身份控制开关默认启用（最高保护）；从持久化配置恢复
topicIngest.IdentityControlEnabled = db.GetSetting("identity_control") != "0";

// 启动时从持久化配置恢复 EMQX 连接（不探测；连通性由采集循环反馈）
var savedUrl = db.GetSetting("emqx_url");
var savedKey = db.GetSetting("emqx_api_key");
var savedSecret = db.GetSetting("emqx_api_secret");
if (!string.IsNullOrEmpty(savedUrl) && !string.IsNullOrEmpty(savedKey) && !string.IsNullOrEmpty(savedSecret))
{
    emqx.SetCredentials(savedUrl, $"{savedKey}:{savedSecret}");
    collector.IsConfigured = true;
}

builder.Services.AddSingleton(db);
builder.Services.AddSingleton(emqx);
builder.Services.AddSingleton(auth);
builder.Services.AddSingleton(health);
builder.Services.AddSingleton(collector);
builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectorService>());
builder.Services.AddSingleton(topicIngest);
builder.Services.AddHostedService(sp => sp.GetRequiredService<TopicIngestService>());
builder.Services.AddSingleton<UpdateProgressTracker>();

// 反代场景（HTTPS 反代 → 内部 HTTP）：信任 X-Forwarded-Proto，让 Secure Cookie 在 HTTPS 下生效
// 伪造 https 头只会影响自身 Cookie 的 Secure 标记，无实际危害
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 2;
});

// Cookie 认证：24h 会话，HttpOnly + Secure（HTTPS 反代下防明文嗅探；HTTP 直连仍可用）
// 注：SameSite 用默认 Lax——实测 Strict 会导致登录 Cookie 不下发（框架兼容问题），
// 且 Lax + X-Frame-Options: DENY 已挡住跨站 POST 与点击劫持
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login.html";
        o.ExpireTimeSpan = TimeSpan.FromHours(24);
        o.SlidingExpiration = false;
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseAuthentication();

// ---- 安全响应头：防点击劫持 / MIME 嗅探 / 外链泄露 Referrer ----
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

// ---- 认证门控：未初始化→/setup，未登录→/login，API 一律 401 ----
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    var authed = ctx.User.Identity?.IsAuthenticated == true;
    var initialized = auth.IsInitialized;

    // 静态资源放行（.css/.js/favicon；登录页/setup 页依赖样式）
    if (path.EndsWith(".css") || path.EndsWith(".js") || path == "/favicon.ico")
    {
        await next();
        return;
    }

    // 主题统计 Webhook（EMQX 规则引擎调用，token 校验在端点内）
    if (path == "/api/ingest")
    {
        await next();
        return;
    }

    if (!initialized)
    {
        if (path == "/setup.html" || path == "/api/setup")
        {
            await next();
            return;
        }
        if (path.StartsWith("/api/"))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "系统未初始化，请先设置管理员账号" });
            return;
        }
        ctx.Response.Redirect("/setup.html");
        return;
    }

    if (!authed)
    {
        if (path == "/login.html" || path == "/api/login")
        {
            await next();
            return;
        }
        if (path.StartsWith("/api/"))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "未登录" });
            return;
        }
        ctx.Response.Redirect("/login.html");
        return;
    }

    // 已登录访问登录/设置页 → 回主页
    if (path == "/login.html" || path == "/setup.html")
    {
        ctx.Response.Redirect("/");
        return;
    }
    await next();
});

app.UseAuthorization();

// ---- 静态文件（嵌入式资源，单文件发布）----
var embeddedFs = new Microsoft.Extensions.FileProviders.EmbeddedFileProvider(
    typeof(Program).Assembly, "EmqxMonitor.wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embeddedFs });
app.UseStaticFiles(new StaticFileOptions { FileProvider = embeddedFs });

// 客户端 IP（登录锁定用；反代场景需显式启用 trust_proxy 才信任 X-Forwarded-For）
// 安全说明：默认直连模式用 TCP 对端 IP——客户端伪造 X-Forwarded-For 无法绕过登录锁定；
// 启用 trust_proxy 后反代必须覆盖该头为真实客户端 IP（见 docs/fas-install-guide.md 安全部署选项）
string ClientIp(HttpContext ctx)
{
    // 环境变量（systemd 配 EMQX_MONITOR_TRUST_PROXY=1）或 settings 表 trust_proxy=1 均可启用
    var trustProxy = Environment.GetEnvironmentVariable("EMQX_MONITOR_TRUST_PROXY") == "1"
                     || db.GetSetting("trust_proxy") == "1";
    if (trustProxy)
    {
        var fwd = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fwd))
            return fwd.Split(',')[0].Trim();
    }
    return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

// ---- 认证 API ----

app.MapGet("/api/status", () => Results.Json(new
{
    ok = true,
    version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "2.0.0",   // 应用版本
    initialized = auth.IsInitialized,
    configured = emqx.IsConfigured,
    collecting = collector.IsConfigured,
    wizard_done = db.GetSetting("wizard_done") == "1",
    last_status = collector.LastStatus,
    last_collect_ok = collector.LastCollectOk,
    last_error = collector.LastError,
    online_clients = collector.LastClientCount,
}));

app.MapPost("/api/setup", (SetupRequest req) =>
{
    var err = auth.Setup(req.Username ?? "", req.Password ?? "");
    if (err != null) return Results.Json(new { ok = false, error = err });
    return Results.Json(new { ok = true });
});

app.MapPost("/api/login", async (LoginRequest req, HttpContext ctx) =>
{
    if (string.IsNullOrEmpty(req.Username) || string.IsNullOrEmpty(req.Password))
        return Results.Json(new { ok = false, error = "请输入用户名和密码" });

    var err = auth.Login(req.Username, req.Password, ClientIp(ctx));
    if (err != null) return Results.Json(new { ok = false, error = err });

    var claims = new[] { new Claim(ClaimTypes.Name, req.Username.Trim()) };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    auth.ResetFailures(req.Username.Trim(), ClientIp(ctx));
    return Results.Json(new { ok = true });
});

app.MapPost("/api/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Json(new { ok = true });
});

app.MapPost("/api/change-password", (ChangePasswordRequest req, HttpContext ctx) =>
{
    var user = ctx.User.Identity?.Name ?? "";
    var err = auth.ChangePassword(user, req.OldPassword ?? "", req.NewPassword ?? "", ClientIp(ctx));
    if (err != null) return Results.Json(new { ok = false, error = err });
    return Results.Json(new { ok = true });
});

// ---- 配置 API ----

app.MapGet("/api/config", () => Results.Json(new
{
    ok = true,
    configured = emqx.IsConfigured,
    emqx_url = db.GetSetting("emqx_url") ?? "",
    listen_port = port,
    data_retention_days = Database.Retention.TotalDays,
    status = collector.LastStatus,
    online_clients = collector.LastClientCount,
    last_collect_ok = collector.LastCollectOk,
}));

app.MapPost("/api/config", async (ConfigRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.EmqxUrl) || string.IsNullOrWhiteSpace(req.ApiKey) || string.IsNullOrWhiteSpace(req.ApiSecret))
        return Results.Json(new { ok = false, error = "EMQX 地址、API Key、API Secret 不能为空" });

    var err = await emqx.ConfigureAsync(req.EmqxUrl, $"{req.ApiKey.Trim()}:{req.ApiSecret.Trim()}");
    if (err != null) return Results.Json(new { ok = false, error = err });

    db.SetSetting("emqx_url", req.EmqxUrl.Trim().TrimEnd('/'));
    db.SetSetting("emqx_api_key", req.ApiKey.Trim());
    db.SetSetting("emqx_api_secret", req.ApiSecret.Trim());
    db.SetSetting("wizard_done", "1");   // EMQX 连接成功 = 首次引导完成
    collector.IsConfigured = true;
    return Results.Json(new { ok = true });
});

app.MapPost("/api/config/disconnect", () =>
{
    collector.IsConfigured = false;
    db.SetSetting("emqx_url", "");
    db.SetSetting("emqx_api_key", "");
    db.SetSetting("emqx_api_secret", "");
    return Results.Json(new { ok = true });
});

// ---- 排行榜 API ----

app.MapGet("/api/leaderboard", (string from, string to, string? order, int? limit, Database database) =>
{
    var (f, t, err) = ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var rows = database.QueryLeaderboard(f, t, order ?? "oct", Math.Clamp(limit ?? 100, 1, 1000));
    return Results.Json(new { ok = true, from = f, to = t, order = order ?? "oct", rows });
});

app.MapGet("/api/leaderboard/{name}", (string name, string from, string to, Database database) =>
{
    var (f, t, err) = ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var rows = database.QueryClientDetail(name, f, t);
    return Results.Json(new { ok = true, name, from = f, to = t, rows });
});

// ---- 健康 API ----

app.MapGet("/api/health", (string from, string to, Database database) =>
{
    var (f, t, err) = ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var rows = database.QueryHealth(f, t);
    return Results.Json(new { ok = true, from = f, to = t, rows });
});

// ---- CSV 导出（带 BOM，Excel 直接打开）----

app.MapGet("/api/export.csv", (string from, string to, string? order, Database database) =>
{
    var (f, t, err) = ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var rows = database.QueryLeaderboard(f, t, order ?? "oct", 5000);
    var sb = new StringBuilder();
    sb.Append("\uFEFF排名,呼号,设备数,总字节,总消息,总包数,重连次数\n");
    for (var i = 0; i < rows.Count; i++)
    {
        var r = rows[i];
        sb.Append($"{i + 1},{Csv(r.Name)},{r.DeviceCount},{r.TotalOct},{r.TotalMsg},{r.TotalPkt},{r.ReconnectCount}\n");
    }
    return Results.Text(sb.ToString(), "text/csv; charset=utf-8");
});

// ---- 主题统计（规则引擎 Webhook）----

// POST /api/ingest — EMQX 规则引擎消息事件入口（token 校验）
// 注意：必须同步 await 读 body——异步 Task.Run 读 Request.Body 在响应返回后不可读
app.MapPost("/api/ingest", async (HttpContext ctx, TopicIngestService ingest) =>
{
    var token = db.GetSetting("ingest_token");
    var got = ctx.Request.Headers["X-Ingest-Token"].FirstOrDefault();
    if (string.IsNullOrEmpty(token) || got == null
        || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(got), System.Text.Encoding.UTF8.GetBytes(token)))
        return Results.Json(new { ok = false, error = "invalid token" }, statusCode: 401);
    if (!ctx.Request.HasJsonContentType())
        return Results.Json(new { ok = false, error = "bad content type" }, statusCode: 400);

    try
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var root = doc.RootElement;
        var topic = root.TryGetProperty("topic", out var t) ? t.GetString() : null;
        var username = root.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        if (username == "undefined") username = null;   // EMQX 无用户名客户端的 Erlang undefined atom 序列化
        // 呼号优先 client_attrs.callsign（认证时服务端写入的属性，比 username 可靠）
        string? callsign = null, uid = null;
        if (root.TryGetProperty("client_attrs", out var ca) && ca.ValueKind == JsonValueKind.Object)
        {
            if (ca.TryGetProperty("callsign", out var cs) && cs.ValueKind == JsonValueKind.String)
                callsign = cs.GetString();
            if (ca.TryGetProperty("uid", out var cu))
            {
                uid = cu.ValueKind == JsonValueKind.String ? cu.GetString() : cu.GetRawText();
            }
        }
        if (!string.IsNullOrEmpty(callsign)) username = callsign;
        var clientid = root.TryGetProperty("clientid", out var c) ? c.GetString() : null;
        long bytes = 0;
        byte[]? raw = null;
        if (root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString()!;
            try
            {
                raw = Convert.FromBase64String(s);
                bytes = raw.Length;
            }
            catch { bytes = s.Length; }   // 非 base64 则按字符数近似
        }
        if (!string.IsNullOrEmpty(topic) && !string.IsNullOrEmpty(clientid))
        {
            ingest.Ingest(topic, username, uid, clientid, bytes, DateTime.Now);
            await RunIdentityAuditAsync(raw, topic, username, uid, clientid, DateTime.Now);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Ingest] 解析失败: {ex.Message}");
    }
    return Results.Json(new { ok = true });
});

// 包头审计（身份控制）：解包 FMO/RAW 包头 → 比对连接身份 → KICK 自动拉黑 / WARN 仅记录 / FAIL 降级
async Task RunIdentityAuditAsync(byte[]? raw, string? topic, string? connCallsign, string? connUid, string? clientid, DateTime now)
{
    if (raw == null || raw.Length == 0) return;   // 无 payload（文本统计模式）不审计
    var parsed = FmoRawParser.Parse(raw);
    var ts = now.ToString("yyyy-MM-dd HH:mm:ss.fff");

    if (!parsed.Ok)
    {
        // FAIL：非法包（长度/len 不符/超 MTU），降级仅记录，不处置；限流防刷库放大
        if (!topicIngest.FailThrottled())
        {
            try { db.WriteAuditPacket(new AuditPacketRow { Ts = ts, Topic = topic ?? "", ClientId = clientid ?? "", Verdict = "FAIL", Len = raw.Length }); }
            catch (Exception ex) { Console.Error.WriteLine($"[Audit] FAIL 落库失败: {ex.Message}"); }
        }
        return;
    }

    var pktCallsign = parsed.Callsign.Trim().ToUpperInvariant();
    var pktUid = parsed.Uid.ToString();
    var connCs = (connCallsign ?? "").Trim().ToUpperInvariant();
    var connU = connUid ?? "";

    string verdict;
    if (string.IsNullOrEmpty(connCs) && string.IsNullOrEmpty(connU))
    {
        verdict = "WARN";   // 连接无身份（匿名）→ 无法比对，仅记录
    }
    else
    {
        var csOk = !string.IsNullOrEmpty(pktCallsign) && pktCallsign == connCs;
        var uidOk = !string.IsNullOrEmpty(pktUid) && pktUid == connU;
        verdict = csOk && uidOk ? "PASS" : "KICK";
    }

    if (verdict == "PASS") return;   // 放行（topic_stats 已聚合）

    // 异常事件：KICK（可自动拉黑）/ WARN
    var ban = false;
    if (verdict == "KICK" && topicIngest.IdentityControlEnabled && !string.IsNullOrEmpty(connCs))
    {
        var reason = $"身份控制: 包头声明 {pktCallsign}(UID {pktUid}) 与连接身份 {connCs}{(connU.Length > 0 ? $"(UID {connU})" : "")} 不符";
        try
        {
            var (err, _) = await emqx.BanAsync(connCs, reason, null);
            if (err == null)
            {
                ban = true;
                try
                {
                    db.AddBlacklistEvent("ban", "username", connCs, reason, null, "身份控制", now);
                    _ = collector.CollectNowAsync();   // 即时刷新在线列表
                }
                catch (Exception ex) { Console.Error.WriteLine($"[Audit] 拉黑留痕失败: {ex.Message}"); }
            }
            else
            {
                Console.Error.WriteLine($"[Audit] 自动拉黑 {connCs} 失败: {err}");
            }
        }
        catch (Exception ex)
        {
            // 防御：EMQX 未配置/URL 异常等不阻断审计落库
            Console.Error.WriteLine($"[Audit] 自动拉黑 {connCs} 异常: {ex.Message}");
        }
    }

    try
    {
        db.WriteAuditPacket(new AuditPacketRow
        {
            Ts = ts,
            Topic = topic ?? "",
            ClientId = clientid ?? "",
            ConnCallsign = connCallsign,
            ConnUid = connUid,
            PktCallsign = pktCallsign,
            PktUid = pktUid,
            Verdict = verdict,
            Len = parsed.Len,
            FrameNum = parsed.FrameNum,
            CrcOk = parsed.CrcOk,
            Smeter = parsed.Smeter,
            SrvUid = parsed.SrvUid.ToString(),
            PktTs = parsed.Timestamp.ToString(),
            StreamBegin = parsed.StreamBeginUtc.ToString(),
            Ban = ban,
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Audit] 事件落库失败: {ex.Message}");
    }
}

// GET /api/topic-config — 主题统计状态
app.MapGet("/api/topic-config", () => Results.Json(new
{
    ok = true,
    enabled = db.GetSetting("topic_enabled") == "1",
    topic = db.GetSetting("topic_name") ?? "FMO/RAW",
    webhook_url = db.GetSetting("topic_webhook_url") ?? "",
    ingest_url = $"http://{GetLanIp()}:{port}/api/ingest",
    local_ips = GetAllLocalIps(),   // 本机所有 IP（Webhook 快速配置按钮）
    total_ingested = topicIngest.TotalIngested,
    last_ingest_at = topicIngest.LastIngestAt == default ? null : topicIngest.LastIngestAt.ToString("yyyy-MM-dd HH:mm:ss"),
    ingest_token = topicIngest.GetToken(db),
    pending = db.GetSetting("topic_pending"),   // 非空 = 有步骤超时待确认（集群场景）
    failed = db.GetSetting("topic_failed"),     // 非空 = 有步骤失败（尽力配置报告）
}));

// POST /api/topic-config — 启用/停用主题统计（自动配置 EMQX 规则引擎）
app.MapPost("/api/topic-config", async (TopicConfigRequest req) =>
{
    var topic = string.IsNullOrWhiteSpace(req.Topic) ? "FMO/RAW" : req.Topic.Trim();
    if (req.Enable)
    {
        if (!emqx.IsConfigured)
            return Results.Json(new { ok = false, error = "请先在 EMQX 连接配置中保存连接" });
        var webhookUrl = string.IsNullOrWhiteSpace(req.WebhookUrl) ? $"http://{GetLanIp()}:{port}/api/ingest" : req.WebhookUrl.Trim().TrimEnd('/');
        var token = topicIngest.GetToken(db);
        // 尽力配置：失败不中断，统一报告；集群下请求状态不可靠，报告以实际查询为准
        var (err, pending, failed) = await emqx.SetupTopicRuleAsync(webhookUrl, token, topic);
        // 立即启用：不阻塞——EMQX 规则引擎一旦生效即主动上报，配置报告只是信息
        db.SetSetting("topic_enabled", "1");
        db.SetSetting("topic_name", topic);
        db.SetSetting("topic_webhook_url", webhookUrl);
        db.SetSetting("topic_pending", pending ?? "");
        db.SetSetting("topic_failed", failed ?? "");
        // 实际状态（EMQX 侧真实查询，权威）
        var status = await emqx.GetTopicRuleStatusAsync();
        if (status.Ok) { db.SetSetting("topic_pending", ""); db.SetSetting("topic_failed", ""); }
        return Results.Json(new
        {
            ok = true,
            enabled = true,   // 立即启用（配置报告不阻塞）
            topic,
            webhook_url = webhookUrl,
            pending,
            failed,
            status = new
            {
                status.Ok,
                connector = new { exists = status.ConnectorExists, state = status.ConnectorStatus, reason = status.ConnectorReason },
                middleware = new { exists = status.MiddlewareExists, kind = status.V6 ? "action" : "bridge" },
                rule = new { exists = status.RuleExists, enabled = status.RuleEnabled },
            },
            hint = status.Ok ? null
                : "主题统计已启用（数据照常接收）。配置存在异常，请到 EMQX Dashboard → 集成 → 连接器/规则 查看，或修复后重新启用/点「测试连接」。",
        });
    }
    else
    {
        var err = await emqx.RemoveTopicRuleAsync();
        if (err != null)
            return Results.Json(new { ok = false, error = err });
        db.SetSetting("topic_enabled", "0");
        db.SetSetting("topic_pending", "");
        db.SetSetting("topic_failed", "");
        return Results.Json(new { ok = true });
    }
});

// GET /api/topic-test — 测试主题统计链路（connector/bridge|action/rule 四件套真实状态）
app.MapGet("/api/topic-test", async () =>
{
    if (!emqx.IsConfigured)
        return Results.Json(new { ok = false, error = "未配置 EMQX 连接" });
    var status = await emqx.GetTopicRuleStatusAsync();
    // 测试通过则清除待确认标记
    if (status.Ok)
        db.SetSetting("topic_pending", "");
    return Results.Json(new
    {
        ok = true,
        status = new
        {
            status.Ok,
            status.V6,
            connector = new { exists = status.ConnectorExists, state = status.ConnectorStatus, reason = status.ConnectorReason },
            middleware = new { exists = status.MiddlewareExists, kind = status.V6 ? "action" : "bridge" },
            rule = new { exists = status.RuleExists, enabled = status.RuleEnabled },
        },
        dashboard_hint = status.Ok
            ? null
            : "链路不完整。请到 EMQX Dashboard → 集成 → 连接器/规则 查看真实状态，或重新启用主题统计",
    });
});

// GET /api/topic-leaderboard — 主题发包量排行（按呼号）
app.MapGet("/api/topic-leaderboard", (string from, string to, string? order, int? limit, Database database) =>
{
    var (f, t, err) = ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var topic = db.GetSetting("topic_name") ?? "FMO/RAW";
    var rows = database.QueryTopicLeaderboard(topic, f, t, order ?? "msg", Math.Clamp(limit ?? 100, 1, 1000));
    return Results.Json(new { ok = true, topic, from = f, to = t, order = order ?? "msg", rows });
});

// GET /api/topic-leaderboard/{name} — 呼号在主题上的 clientid 明细
app.MapGet("/api/topic-leaderboard/{name}", (string name, string from, string to, Database database) =>
{
    var (f, t, err) = ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var topic = db.GetSetting("topic_name") ?? "FMO/RAW";
    var rows = database.QueryTopicDetail(topic, name, f, t);
    return Results.Json(new { ok = true, name, topic, from = f, to = t, rows });
});

// GET /api/topic-timeline — 时间轴（全员总量按时间桶聚合，1m/5m/1h）
app.MapGet("/api/topic-timeline", (string from, string to, string? bucket, Database database) =>
{
    var (f, t, err) = ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var topic = db.GetSetting("topic_name") ?? "FMO/RAW";
    var b = bucket is "10s" or "5m" or "1h" ? bucket : "1m";
    var rows = database.QueryTopicTimeline(topic, f, t, b);
    if (rows.Count > 40000)
        return Results.Json(new { ok = false, error = "时间范围过大（补零后超过 4 万点）。请缩小时间范围或使用更粗的统计周期（如 1 小时）" });
    return Results.Json(new { ok = true, topic, from = f, to = t, bucket = b, rows });
});

// GET /api/topic-export.csv — 主题排行 CSV 导出
app.MapGet("/api/topic-export.csv", (string from, string to, string? order, Database database) =>
{
    var (f, t, err) = ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var topic = db.GetSetting("topic_name") ?? "FMO/RAW";
    var rows = database.QueryTopicLeaderboard(topic, f, t, order ?? "msg", 5000);
    var sb = new StringBuilder();
    sb.Append($"\uFEFF排名,呼号,设备数,消息数,字节数,主题\n");
    for (var i = 0; i < rows.Count; i++)
    {
        var r = rows[i];
        sb.Append($"{i + 1},{Csv(r.Name)},{r.DeviceCount},{r.TotalMsg},{r.TotalBytes},{Csv(topic)}\n");
    }
    return Results.Text(sb.ToString(), "text/csv; charset=utf-8");
});

/// <summary>获取本机局域网 IP（用于生成默认 webhook 地址）</summary>
static string GetLanIp()
{
    try
    {
        using var sock = new System.Net.Sockets.UdpClient("8.8.8.8", 80);   // 不发包，仅触发路由选择
        var ip = (sock.Client.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();
        return string.IsNullOrEmpty(ip) || ip == "0.0.0.0" ? "127.0.0.1" : ip;
    }
    catch
    {
        return "127.0.0.1";
    }
}

// 本机所有非回环 IPv4（Webhook 快速配置用：Linux 多网卡常见多 IP，如 eth0/eth1/docker0）
static List<string> GetAllLocalIps()
{
    var list = new List<string>();
    try
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var ip = addr.Address.ToString();
                if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;   // 回环 / 链路本地
                if (!list.Contains(ip)) list.Add(ip);
            }
        }
    }
    catch { }
    return list;
}

// ---- 兼容性自检 ----

// GET /api/check — EMQX 版本 + 关键 API 探测
app.MapGet("/api/check", async () =>
{
    if (!emqx.IsConfigured)
        return Results.Json(new { ok = false, error = "未配置 EMQX 连接" });
    var report = await emqx.CheckCompatibilityAsync();
    return Results.Json(new
    {
        ok = true,
        version = report.Version,
        supported = report.Supported,
        suggested_upgrade = report.SuggestedUpgrade,
        checks = report.Checks.Select(c => new { c.Name, c.Path, c.Ok, c.Note }),
    });
});

// GET /api/online — 当前在线客户端列表（采集器 60s 缓存，不重复打 EMQX）
app.MapGet("/api/online", () =>
{
    var list = collector.LastClients;
    return Results.Json(new
    {
        ok = true,
        collecting = collector.IsConfigured,
        // 数据新鲜度：采集时间（本地）
        updated_at = collector.LastClientsAt?.ToString("yyyy-MM-dd HH:mm:ss"),
        total = list.Count,
        rows = list.Select(c => new
        {
            // 呼号优先 client_attrs.callsign；username "undefined"（Erlang atom 序列化）归一为 null；都没有 → 匿名
            name = c.Callsign ?? NormalizeUser(c.Username),
            uid = c.Uid,
            is_anonymous = string.IsNullOrEmpty(c.Callsign) && string.IsNullOrEmpty(NormalizeUser(c.Username)),
            clientid = c.ClientId,
            ip = c.IpAddress,
            connected_at = c.ConnectedAt,       // RFC3339，前端转本地显示
            send_oct = c.SendOct,
            recv_oct = c.RecvOct,
            send_msg = c.SendMsg,
            recv_msg = c.RecvMsg,
        }),
    });

    static string? NormalizeUser(string? u) => u == "undefined" ? null : u;
});

// ---- 黑名单 API（拉黑/解封/当前生效/操作历史）----
// 权威执行在 EMQX（banned API），本地 blacklist_audit 留痕；EMQX 操作失败不写流水。

// POST /api/blacklist/ban — 拉黑呼号（username 粒度）+ 立即踢下线 + 留痕
app.MapPost("/api/blacklist/ban", async (BlacklistBanRequest req, HttpContext ctx) =>
{
    var who = req.Who?.Trim() ?? "";
    if (string.IsNullOrEmpty(who))
        return Results.Json(new { ok = false, error = "呼号不能为空" });
    if (!emqx.IsConfigured)
        return Results.Json(new { ok = false, error = "未配置 EMQX 连接，无法执行拉黑" });

    // 到期时间：本地 yyyy-MM-ddTHH:mm → RFC3339（含 +08:00 偏移，EMQX 自动转 UTC 存储）
    string? untilRfc = null, untilLocal = null;
    if (!string.IsNullOrWhiteSpace(req.Until))
    {
        if (!DateTime.TryParseExact(req.Until, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until))
            return Results.Json(new { ok = false, error = "到期时间格式应为 yyyy-MM-ddTHH:mm" });
        if (until <= DateTime.Now)
            return Results.Json(new { ok = false, error = "到期时间必须晚于当前时间" });
        var offset = TimeZoneInfo.Local.GetUtcOffset(until);
        untilRfc = until.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ssK").Replace("+00:00", $"{(offset >= TimeSpan.Zero ? "+" : "-")}{offset:hh\\:mm}");
        untilLocal = until.ToString("yyyy-MM-dd HH:mm:ss");
    }

    var (err, kicked) = await emqx.BanAsync(who, req.Reason?.Trim() is { Length: > 0 } r ? r : null, untilRfc);
    if (err != null)
        return Results.Json(new { ok = false, error = $"拉黑失败: {err}" });

    db.AddBlacklistEvent("ban", "username", who, req.Reason?.Trim(), untilLocal,
        ctx.User.Identity?.Name ?? "?", DateTime.Now);
    // 拉黑后即时刷新在线列表缓存（被踢的客户端立即从"在线"消失）
    _ = collector.CollectNowAsync();
    return Results.Json(new { ok = true, who, kicked, until = untilLocal });
});

// POST /api/blacklist/unban — 解封呼号 + 留痕
app.MapPost("/api/blacklist/unban", async (BlacklistUnbanRequest req, HttpContext ctx) =>
{
    var who = req.Who?.Trim() ?? "";
    if (string.IsNullOrEmpty(who))
        return Results.Json(new { ok = false, error = "呼号不能为空" });
    if (!emqx.IsConfigured)
        return Results.Json(new { ok = false, error = "未配置 EMQX 连接，无法执行解封" });

    var err = await emqx.UnbanAsync(who);
    if (err != null)
        return Results.Json(new { ok = false, error = $"解封失败: {err}" });

    db.AddBlacklistEvent("unban", "username", who, null, null,
        ctx.User.Identity?.Name ?? "?", DateTime.Now);
    _ = collector.CollectNowAsync();   // 即时刷新在线列表缓存
    return Results.Json(new { ok = true, who });
});

// GET /api/blacklist/active — 当前生效黑名单（本地推导）+ EMQX 侧对照（用于发现 EMQX 上手动拉的黑名单）
app.MapGet("/api/blacklist/active", async () =>
{
    var local = db.QueryActiveBlacklist(DateTime.Now);
    var emqxBanned = emqx.IsConfigured ? await emqx.GetBannedAsync() : [];
    return Results.Json(new
    {
        ok = true,
        // 本地生效：带全字段（操作人/时间/到期）
        local,
        // EMQX 侧存在但本地无记录（可能是 EMQX 上手动拉黑）→ 前端提示"来源: EMQX"
        emqx_only = emqxBanned.Where(b => !local.Any(l => string.Equals(l.Who, b.Who, StringComparison.OrdinalIgnoreCase)))
                              .Select(b => new { b.Who, b.Reason, b.Until, b.By }),
        emqx_reachable = emqx.IsConfigured,
    });
});

// GET /api/blacklist/history — 全部操作流水（倒序）
app.MapGet("/api/blacklist/history", (int? limit, Database database) =>
    Results.Json(new { ok = true, rows = database.QueryBlacklistHistory(Math.Clamp(limit ?? 200, 1, 1000)) }));

// GET /api/identity-control — 身份控制开关状态（默认启用）
app.MapGet("/api/identity-control", () => Results.Json(new
{
    ok = true,
    enabled = topicIngest.IdentityControlEnabled,
}));

// POST /api/identity-control — 设置身份控制开关（关闭 = 降级为仅标记提醒，不自动拉黑）
app.MapPost("/api/identity-control", (IdentityControlRequest req) =>
{
    topicIngest.IdentityControlEnabled = req.Enabled;
    db.SetSetting("identity_control", req.Enabled ? "1" : "0");
    return Results.Json(new { ok = true, enabled = req.Enabled });
});

// GET /api/audit-packets — 包头审计事件（KICK/WARN/FAIL）
app.MapGet("/api/audit-packets", (string from, string to, string? verdict, int? limit, Database database) =>
{
    var (f, t, err) = ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var rows = database.QueryAuditPackets(f, t, verdict, Math.Clamp(limit ?? 200, 1, 1000));
    var counts = database.CountAuditVerdicts(f, t);
    return Results.Json(new { ok = true, from = f, to = t, rows, counts });
});

// ---- 版本与更新（OTA）----

// GET /api/update/check — 检查更新（当前/最新/模式）
app.MapGet("/api/update/check", async () =>
{
    var mode = UpdateService.DetectMode();
    var (cur, latest, has, err) = await UpdateService.CheckAsync();
    return Results.Json(new
    {
        ok = true,
        current = cur,
        latest,
        has_update = has,
        update_mode = UpdateService.ModeName(mode),
        docker_hint = mode == UpdateMode.Docker
            ? "当前为 Docker 部署，不支持自更新。请使用: docker pull 新镜像 && docker compose up -d"
            : null,
        error = err,
    });
});

// POST /api/update/apply — 启动更新（立即返回，进度由 /api/update/progress 轮询；成功后以非 0 退出码退出触发自动重启）
app.MapPost("/api/update/apply", (UpdateProgressTracker tracker) =>
{
    if (UpdateService.DetectMode() == UpdateMode.Docker)
        return Results.Json(new { ok = false, error = "容器内不支持自更新，请使用 docker pull 更新镜像" });

    // 后台执行下载+替换，进度写入 tracker 供前端轮询；退出码非 0 触发 systemd/计划任务自动重启
    tracker.Reset();
    _ = Task.Run(async () =>
    {
        var (err, msg, replaced) = await UpdateService.ApplyAsync(p => tracker.Report(p));
        if (err != null)
        {
            tracker.Report(new UpdateProgress { Stage = "error", Message = err });
        }
        else if (!replaced)
        {
            // 已是最新版本 → 不退出
            tracker.Report(new UpdateProgress { Stage = "done", Message = msg });
        }
        else
        {
            // 替换脚本已就绪（ApplyAsync 内部已报 ready），延迟退出让脚本覆盖二进制
            await Task.Delay(1500);
            Environment.Exit(1);
        }
    });
    return Results.Json(new { ok = true, started = true });
});

// GET /api/update/progress — 查询更新进度（网页 300ms 轮询）
app.MapGet("/api/update/progress", (UpdateProgressTracker tracker) =>
{
    var p = tracker.Snapshot();
    return Results.Json(new
    {
        ok = true,
        stage = p.Stage,
        percent = p.Percent,
        bytes_read = p.BytesRead,
        total_bytes = p.TotalBytes,
        message = p.Message,
    });
});

// ---- 数据管理 ----

// GET /api/admin/stats — 各表数据量
app.MapGet("/api/admin/stats", (Database database) =>
{
    var (minutes, topics, health, audit) = database.CountRows();
    return Results.Json(new { ok = true, minute_stats = minutes, topic_stats = topics, health_snapshots = health, audit_packets = audit });
});

// POST /api/admin/clear-data — 一键清空统计数据（保留配置和管理员）
app.MapPost("/api/admin/clear-data", (Database database) =>
{
    var (minutes, topics, health, audit) = database.ClearAllData();
    return Results.Json(new { ok = true, cleared = new { minute_stats = minutes, topic_stats = topics, health_snapshots = health, audit_packets = audit } });
});

// POST /api/admin/reset — 完全重置审计监控工具（清空全部数据+配置+管理员，停用规则引擎，回到首次安装）
app.MapPost("/api/admin/reset", async () =>
{
    // 1) 清理 EMQX 上的规则引擎（避免残留规则用旧 token 继续转发）
    if (emqx.IsConfigured)
    {
        var err = await emqx.RemoveTopicRuleAsync();
        if (err != null)
            return Results.Json(new { ok = false, error = $"清理 EMQX 规则引擎失败: {err}（可稍后手动在 EMQX 删除 emqx-monitor-* 资源）" });
    }
    // 2) 停止采集并清空内存凭据（configured 状态必须归零，引导才能正确触发）
    collector.IsConfigured = false;
    emqx.ClearCredentials();
    // 3) 清空采集器/接收器内存状态（计数器基线、聚合缓冲、计数归零）
    collector.ResetState();
    topicIngest.Reset();
    // 4) 清空全部表
    db.ClearAll();
    return Results.Json(new { ok = true });
});

// ---- 时间范围解析：yyyy-MM-ddTHH:mm（服务器本地时间），跨度≤31 天 ----
static (string From, string To, string? Error) ParseRange(string from, string to)
{
    if (!DateTime.TryParseExact(from, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
        || !DateTime.TryParseExact(to, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        return ("", "", "时间格式应为 yyyy-MM-ddTHH:mm");
    if (t < f) return ("", "", "结束时间不能早于开始时间");
    if (t - f > TimeSpan.FromDays(31)) return ("", "", "时间跨度不能超过 31 天");
    return (f.ToString("yyyy-MM-dd HH:mm:00"), t.ToString("yyyy-MM-dd HH:mm:00"), null);
}

static string Csv(string v)
{
    // CSV 公式注入防护：以 = + - @ \t \r 开头的值前缀 '（Excel 打开时不执行）
    if (v.Length > 0 && (v[0] == '=' || v[0] == '+' || v[0] == '-' || v[0] == '@' || v[0] == '\t' || v[0] == '\r'))
        v = "'" + v;
    return v.Contains(',') || v.Contains('"') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
}

app.Run();

/// <summary>更新进度追踪器（网页 OTA 轮询读当前状态）</summary>
public sealed class UpdateProgressTracker
{
    private readonly object _lock = new();
    private UpdateProgress _current = new() { Stage = "idle" };
    public void Reset() { lock (_lock) _current = new() { Stage = "idle" }; }
    public void Report(UpdateProgress p) { lock (_lock) _current = p; }
    public UpdateProgress Snapshot() { lock (_lock) return _current; }
}

record SetupRequest(string? Username, string? Password);
record LoginRequest(string? Username, string? Password);
record ConfigRequest(string? EmqxUrl, string? ApiKey, string? ApiSecret);
record ChangePasswordRequest(string? OldPassword, string? NewPassword);
record TopicConfigRequest(bool Enable, string? Topic, string? WebhookUrl);
record BlacklistBanRequest(string? Who, string? Reason, string? Until);
record BlacklistUnbanRequest(string? Who);
record IdentityControlRequest(bool Enabled);
