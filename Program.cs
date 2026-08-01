using EmqxMonitor;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Globalization;
using System.Security.Claims;
using System.Text;

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

var db = new Database(dbPath);
var emqx = new EmqxClient();
var auth = new AuthService(db);
var health = new HostHealthCollector();
var collector = new CollectorService(emqx, db, health);

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

// Cookie 认证：24h 会话，HttpOnly
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login.html";
        o.ExpireTimeSpan = TimeSpan.FromHours(24);
        o.SlidingExpiration = false;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();

// ---- 认证门控：未初始化→/setup，未登录→/login，API 一律 401 ----
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    var authed = ctx.User.Identity?.IsAuthenticated == true;
    var initialized = auth.IsInitialized;

    // 静态资源放行
    if (path.StartsWith("/css/") || path.StartsWith("/js/") || path == "/favicon.ico")
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

// 客户端 IP（登录锁定用；反代后取 X-Forwarded-For）
static string ClientIp(HttpContext ctx)
{
    var fwd = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(fwd))
        return fwd.Split(',')[0].Trim();
    return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

// ---- 认证 API ----

app.MapGet("/api/status", () => Results.Json(new
{
    ok = true,
    initialized = auth.IsInitialized,
    configured = emqx.IsConfigured,
    collecting = collector.IsConfigured,
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
    auth.ResetFailures(ClientIp(ctx));
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

static string Csv(string v) => v.Contains(',') || v.Contains('"') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

app.Run();

record SetupRequest(string? Username, string? Password);
record LoginRequest(string? Username, string? Password);
record ConfigRequest(string? EmqxUrl, string? ApiKey, string? ApiSecret);
record ChangePasswordRequest(string? OldPassword, string? NewPassword);
