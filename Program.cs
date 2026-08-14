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
        if (err != null)
        {
            Console.WriteLine($"更新失败: {err}");
            if (msg != null) Console.WriteLine(msg);
            Environment.Exit(1);
            return;
        }
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

// ---- CLI 命令：--configure 一次性配置（保存 EMQX 连接 + 自动设置主题统计 bridge）----
if (args.Contains("--configure"))
{
    var (err, msg) = await CliConfigure.RunAsync();
    Console.WriteLine(err ?? msg ?? "");
    Environment.Exit(err != null ? 1 : 0);
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

// 手动构造的服务用独立 LoggerFactory（输出 stdout/stderr，systemd 捕获进 journalctl / 计划任务进 fas.log）
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
var db = new Database(dbPath);
var settings = new AppSettings(db);
var emqx = new EmqxClient();
var auth = new AuthService(db);
var health = new HostHealthCollector();
var collector = new CollectorService(emqx, db, health, loggerFactory.CreateLogger<CollectorService>());
var topicIngest = new TopicIngestService(db);
// 身份控制开关默认启用（最高保护）；从持久化配置恢复
topicIngest.IdentityControlEnabled = settings.IdentityControlEnabled;

// 启动时从持久化配置恢复 EMQX 连接（不探测；连通性由采集循环反馈）
var savedUrl = settings.EmqxUrl;
var savedKey = settings.EmqxApiKey;
var savedSecret = settings.EmqxApiSecret;
if (!string.IsNullOrEmpty(savedUrl) && !string.IsNullOrEmpty(savedKey) && !string.IsNullOrEmpty(savedSecret))
{
    emqx.SetCredentials(savedUrl, $"{savedKey}:{savedSecret}");
    collector.IsConfigured = true;
}

builder.Services.AddSingleton(db);
builder.Services.AddSingleton(settings);
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
                     || settings.TrustProxy;
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
    wizard_done = settings.WizardDone,
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
    emqx_url = settings.EmqxUrl,
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

    settings.EmqxUrl = req.EmqxUrl.Trim().TrimEnd('/');
    settings.EmqxApiKey = req.ApiKey.Trim();
    settings.EmqxApiSecret = req.ApiSecret.Trim();
    settings.WizardDone = true;   // EMQX 连接成功 = 首次引导完成
    collector.IsConfigured = true;
    return Results.Json(new { ok = true });
});

app.MapPost("/api/config/disconnect", () =>
{
    collector.IsConfigured = false;
    settings.EmqxUrl = "";
    settings.EmqxApiKey = "";
    settings.EmqxApiSecret = "";
    return Results.Json(new { ok = true });
});

// ---- 排行榜 API ----

app.MapGet("/api/leaderboard", (string from, string to, string? order, int? limit, Database database) =>
{
    var (f, t, err) = WebHelpers.ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var rows = database.QueryLeaderboard(f, t, order ?? "oct", Math.Clamp(limit ?? 100, 1, 1000));
    return Results.Json(new { ok = true, from = f, to = t, order = order ?? "oct", rows });
});

app.MapGet("/api/leaderboard/{name}", (string name, string from, string to, Database database) =>
{
    var (f, t, err) = WebHelpers.ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var rows = database.QueryClientDetail(name, f, t);
    return Results.Json(new { ok = true, name, from = f, to = t, rows });
});

// ---- 健康 API ----

app.MapGet("/api/health", (string from, string to, Database database) =>
{
    var (f, t, err) = WebHelpers.ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var rows = database.QueryHealth(f, t);
    return Results.Json(new { ok = true, from = f, to = t, rows });
});

// ---- CSV 导出（带 BOM，Excel 直接打开）----

app.MapGet("/api/export.csv", (string from, string to, string? order, Database database) =>
{
    var (f, t, err) = WebHelpers.ParseRange(from, to);
    if (err != null) return Results.Json(new { ok = false, error = err });
    var rows = database.QueryLeaderboard(f, t, order ?? "oct", 5000);
    var sb = new StringBuilder();
    sb.Append("\uFEFF排名,呼号,设备数,总字节,总消息,总包数,重连次数\n");
    for (var i = 0; i < rows.Count; i++)
    {
        var r = rows[i];
        sb.Append($"{i + 1},{WebHelpers.Csv(r.Name)},{r.DeviceCount},{r.TotalOct},{r.TotalMsg},{r.TotalPkt},{r.ReconnectCount}\n");
    }
    return Results.Text(sb.ToString(), "text/csv; charset=utf-8");
});

// ---- 主题统计（规则引擎 Webhook）----
app.MapTopicEndpoints(settings, port, topicIngest, emqx, db, collector);

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
app.MapBlacklistEndpoints(emqx, db, collector, topicIngest, settings);

// ---- 版本与更新（OTA）----
app.MapUpdateEndpoints();

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
