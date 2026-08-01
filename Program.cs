using EmqxMonitor;
using System.Net;
using System.Net.Sockets;

const int PortBase = 9527;
const int PortMax = 9546;   // 最多尝试 20 个端口

// ---- 端口解析：重复启动时打开已有实例，端口被占时自动换 ----
var port = await ResolvePortAsync();
if (port == -2) return;   // 已有实例在跑：已打开浏览器，本进程退出
if (port < 0)
{
    Console.WriteLine("错误：无法找到空闲端口（9527-9546 均被占用）");
    return;
}

// 单文件模式下 wwwroot 内嵌于 exe，运行时自解压到 AppContext.BaseDirectory；
// ContentRoot 必须指向它，否则静态文件 404
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

// 数据库放固定用户数据目录（单文件自解压目录是随机路径，重启会丢数据）
var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "EmqxMonitor");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "emqx-monitor.db");
var db = new Database(dbPath);
var emqx = new EmqxClient();
var snapshotService = new SnapshotService(emqx, db);

builder.Services.AddSingleton(db);
builder.Services.AddSingleton(emqx);
builder.Services.AddSingleton(snapshotService);
builder.Services.AddHostedService(sp => sp.GetRequiredService<SnapshotService>());

var app = builder.Build();

// 静态文件（前端面板）——从程序集嵌入资源读取（单文件发布不依赖外部 wwwroot）
var embeddedFs = new Microsoft.Extensions.FileProviders.EmbeddedFileProvider(
    typeof(Program).Assembly, "EmqxMonitor.wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embeddedFs });
app.UseStaticFiles(new StaticFileOptions { FileProvider = embeddedFs });

// POST /api/config — 配置 EMQX 连接并验证连通性
app.MapPost("/api/config", async (ConfigRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Address) || string.IsNullOrWhiteSpace(req.ApiKey) || string.IsNullOrWhiteSpace(req.ApiSecret))
        return Results.Json(new { ok = false, error = "地址、API Key、API Secret 不能为空" });

    // EMQX Basic Auth 要求 key:secret，后端拼装，用户无需手动合并
    var err = await emqx.ConfigureAsync(req.Address, $"{req.ApiKey.Trim()}:{req.ApiSecret.Trim()}");
    if (err != null)
        return Results.Json(new { ok = false, error = err });

    snapshotService.SetConfigured(true);
    return Results.Json(new { ok = true });
});

// POST /api/disconnect — 停止监控（清空配置）
app.MapPost("/api/disconnect", () =>
{
    snapshotService.SetConfigured(false);
    return Results.Json(new { ok = true });
});

// GET /api/status — 前端轮询时先探活
app.MapGet("/api/status", () => Results.Json(new
{
    ok = true,
    configured = emqx.IsConfigured,
    last_poll_at = snapshotService.LastPollAt?.ToString("HH:mm:ss"),
    last_poll_ok = snapshotService.LastPollOk,
    last_error = snapshotService.LastError
}));

// GET /api/snapshot — 返回最近一次轮询的聚合+异常数据（5s 缓存）
app.MapGet("/api/snapshot", (SnapshotService service) =>
{
    if (!emqx.IsConfigured)
        return Results.Json(new { ok = false, error = "未配置 EMQX 连接" });
    if (service.LatestSnapshot == null)
        return Results.Json(new { ok = false, error = "等待首次轮询…" });

    var s = service.LatestSnapshot;
    return Results.Json(new
    {
        ok = true,
        snapshot_time = s.SnapshotTime,
        online_users = s.OnlineUsers,
        offline_users = s.OfflineUsers,
        users = s.Users
    });
});

// GET /api/history/{username}?range=1h|6h|24h — 趋势数据（分钟聚合）
app.MapGet("/api/history/{username}", (string username, string range, Database database) =>
{
    var hours = range switch
    {
        "6h" => 6,
        "24h" => 24,
        "72h" => 72,
        _ => 1
    };
    var to = DateTime.Now;
    var from = to.AddHours(-hours);

    // 1h 内查原始 5s 快照（未聚合部分），更早查分钟表
    var rawFrom = to.AddHours(-1);
    var rawFromClamped = from > rawFrom ? from : rawFrom;
    var raw = database.QueryTrendRaw(username, rawFromClamped, to);
    var minutes = database.QueryTrendMinute(username, from, rawFromClamped.AddSeconds(-1));

    // 原始快照转分钟粒度（每个 clientid 单独算 末值-初值，再按分钟汇总）
    var minuteMap = new SortedDictionary<string, (long rp, long sp, long rm, long sm, long ro, long so)>();
    foreach (var m in minutes)
        minuteMap[m.Minute] = (m.RecvPkt, m.SendPkt, m.RecvMsg, m.SendMsg, m.RecvOct, m.SendOct);

    // 按 clientid 分组：组内按时间顺序做 末值-初值
    foreach (var group in raw.GroupBy(t => t.ClientId))
    {
        long? prevRp = null, prevSp = null, prevRm = null, prevSm = null, prevRo = null, prevSo = null;
        string? prevMinute = null;
        foreach (var t in group)
        {
            var minute = t.Time[..16];
            if (prevMinute == minute && prevRp != null)
            {
                var cur = GetBucket(minuteMap, minute);
                var (prp, psp, prm, psm, pro, pso) =
                    (prevRp.Value, prevSp!.Value, prevRm!.Value, prevSm!.Value, prevRo!.Value, prevSo!.Value);
                minuteMap[minute] = (
                    cur.rp + Math.Max(0, t.RecvPkt - prp),
                    cur.sp + Math.Max(0, t.SendPkt - psp),
                    cur.rm + Math.Max(0, t.RecvMsg - prm),
                    cur.sm + Math.Max(0, t.SendMsg - psm),
                    cur.ro + Math.Max(0, t.RecvOct - pro),
                    cur.so + Math.Max(0, t.SendOct - pso));
            }
            else
            {
                _ = GetBucket(minuteMap, minute); // 该分钟第一行：不累计（等下一行算 delta）
            }
            prevRp = t.RecvPkt; prevSp = t.SendPkt; prevRm = t.RecvMsg; prevSm = t.SendMsg;
            prevRo = t.RecvOct; prevSo = t.SendOct; prevMinute = minute;
        }
    }

    var points = minuteMap.Select(kv => new
    {
        time = kv.Key,               // yyyy-MM-dd HH:mm（完整时间戳，前端按 range 格式化）
        recv_pkt = kv.Value.rp,
        send_pkt = kv.Value.sp,
        recv_msg = kv.Value.rm,
        send_msg = kv.Value.sm,
        recv_oct = kv.Value.ro,
        send_oct = kv.Value.so
    }).ToList();

    return Results.Json(new { ok = true, username, range, points });
});

// GET /api/debug — 调试：detector 内部状态
app.MapGet("/api/debug", () =>
{
    var dbg = new List<object>();
    foreach (var u in snapshotService.LatestSnapshot?.Users ?? [])
    {
        dbg.Add(new { u.Username, u.TotalRecvPkt, u.TotalSendPkt, u.RateRecvPps, u.RateSendPps });
    }
    return Results.Json(dbg);
});

// GET /api/history/{username}/sessions — 历史上线/下线记录
app.MapGet("/api/history/{username}/sessions", (string username, string range, Database database) =>
{
    var hours = range switch
    {
        "6h" => 6,
        "24h" => 24,
        "72h" => 72,
        _ => 1
    };
    var to = DateTime.Now;
    var from = to.AddHours(-hours);

    var sessions = database.QuerySessions(username, from, to);
    var result = sessions.Select(s => new
    {
        start = s.Start.ToString("yyyy-MM-dd HH:mm:ss"),
        end = s.End?.ToString("yyyy-MM-dd HH:mm:ss"),
        online = s.End == null,
        duration_min = Math.Round(s.DurationSeconds / 60.0, 1)
    }).ToList();

    return Results.Json(new { ok = true, username, range, count = result.Count, sessions = result });
});

// 启动后自动打开默认浏览器（延迟等 Kestrel 就绪；失败静默，不阻塞服务）
_ = Task.Run(async () =>
{
    await Task.Delay(800);
    OpenBrowser(port);
});

// ---- 端口解析与浏览器打开 ----

/// <summary>解析可用端口。返回 -2 = 已有实例运行（已处理）；-1 = 无空闲端口；否则返回端口号</summary>
static async Task<int> ResolvePortAsync()
{
    for (int p = PortBase; p <= PortMax; p++)
    {
        if (!await CanBindAsync(p))
        {
            // 端口被占用：检查是不是本程序旧实例（响应 /api/status 且含 ok 标记）
            if (await IsOurInstanceAsync(p))
            {
                Console.WriteLine($"检测到监控面板已在运行（http://127.0.0.1:{p}），打开浏览器并退出本进程");
                OpenBrowser(p);
                return -2;
            }
            continue;   // 被其他程序占用，试下一个端口
        }
        return p;
    }
    return -1;
}

/// <summary>尝试绑定端口（成功即空闲）</summary>
static async Task<bool> CanBindAsync(int port)
{
    try
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
        return true;
    }
    catch
    {
        return false;
    }
}

/// <summary>探测端口上是否运行着本程序（/api/status 返回含 ok 的 JSON）</summary>
static async Task<bool> IsOurInstanceAsync(int port)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        var resp = await client.GetAsync($"http://127.0.0.1:{port}/api/status");
        if (!resp.IsSuccessStatusCode) return false;
        var body = await resp.Content.ReadAsStringAsync();
        return body.Contains("\"ok\"", StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}

/// <summary>用默认浏览器打开面板地址（失败静默）</summary>
static void OpenBrowser(int port)
{
    try
    {
        var url = $"http://127.0.0.1:{port}";
        if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
        else if (OperatingSystem.IsLinux())
            System.Diagnostics.Process.Start("xdg-open", url);
        else if (OperatingSystem.IsMacOS())
            System.Diagnostics.Process.Start("open", url);
    }
    catch { }
}

app.Run();

static (long rp, long sp, long rm, long sm, long ro, long so) GetBucket(
    SortedDictionary<string, (long rp, long sp, long rm, long sm, long ro, long so)> map, string minute)
    => map.TryGetValue(minute, out var b) ? b : (0, 0, 0, 0, 0, 0);

record ConfigRequest(string Address, string ApiKey, string ApiSecret);
