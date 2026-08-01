using EmqxMonitor;

const int Port = 9527;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");

// 数据库（exe 同目录）
var dbPath = Path.Combine(AppContext.BaseDirectory, "emqx-monitor.db");
var db = new Database(dbPath);
var emqx = new EmqxClient();
var snapshotService = new SnapshotService(emqx, db);

builder.Services.AddSingleton(db);
builder.Services.AddSingleton(emqx);
builder.Services.AddSingleton(snapshotService);
builder.Services.AddHostedService(sp => sp.GetRequiredService<SnapshotService>());

var app = builder.Build();

// 静态文件（前端面板）
app.UseDefaultFiles();
app.UseStaticFiles();

// POST /api/config — 配置 EMQX 连接并验证连通性
app.MapPost("/api/config", async (ConfigRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Address) || string.IsNullOrWhiteSpace(req.ApiKey))
        return Results.Json(new { ok = false, error = "地址和 API Key 不能为空" });

    var err = await emqx.ConfigureAsync(req.Address, req.ApiKey);
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

app.Run();

static (long rp, long sp, long rm, long sm, long ro, long so) GetBucket(
    SortedDictionary<string, (long rp, long sp, long rm, long sm, long ro, long so)> map, string minute)
    => map.TryGetValue(minute, out var b) ? b : (0, 0, 0, 0, 0, 0);

record ConfigRequest(string Address, string ApiKey);
