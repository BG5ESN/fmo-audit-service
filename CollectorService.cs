namespace EmqxMonitor;

/// <summary>
/// 1 分钟采集服务（后台托管服务）：
///  1. 拉取 EMQX 客户端列表 → 与上一轮累计计数器做差 → 写 minute_stats（增量三坑见下）
///  2. 健康采集（宿主机 + EMQX 节点/消息速率/告警）→ 写 health_snapshots
///  3. 每 10 分钟清理 30 天前的过期数据
/// 增量三坑：
///  - 离线客户端从 API 消失，必须在在线时算好 delta 落库
///  - 重连后计数器归零 → delta 为负 → clamp 0 + 标记 reconnect
///  - 新出现的客户端首分钟不计（避免把历史累计算进第一分钟）
/// </summary>
public class CollectorService : BackgroundService
{
    private readonly EmqxClient _emqx;
    private readonly Database _db;
    private readonly IHostHealthCollector _health;

    private readonly object _lock = new();
    // clientid → 上一轮累计计数器
    private readonly Dictionary<string, (long SendOct, long RecvOct, long SendMsg, long RecvMsg, long SendPkt, long RecvPkt)> _prev = new();

    private long? _lastMsgTotal;
    private DateTime _lastMsgAt;
    private DateTime _lastCleanupAt = DateTime.MinValue;

    public CollectorService(EmqxClient emqx, Database db, IHostHealthCollector health)
    {
        _emqx = emqx;
        _db = db;
        _health = health;
    }

    /// <summary>是否已配置 EMQX 连接（由配置页控制）</summary>
    public volatile bool IsConfigured;

    public DateTime? LastCollectAt { get; private set; }
    public bool LastCollectOk { get; private set; }
    public string? LastError { get; private set; }
    public int LastClientCount { get; private set; }
    public string? LastStatus { get; private set; }

    /// <summary>重置采集器内存状态（完全重置时调用）：清空计数器基线，新周期从零开始</summary>
    public void ResetState()
    {
        lock (_lock)
        {
            _prev.Clear();
        }
        _lastMsgTotal = null;
        LastCollectAt = null;
        LastCollectOk = false;
        LastError = null;
        LastClientCount = 0;
        LastStatus = null;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (IsConfigured)
                await CollectAsync();
        }
    }

    private async Task CollectAsync()
    {
        try
        {
            var now = DateTime.Now;   // 服务器本地时间存储（管理员查询/对照投诉时间直观）
            var ts = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0)
                .ToString("yyyy-MM-dd HH:mm:00");

            // ---- 1) 客户端增量采集 ----
            var result = await _emqx.GetClientsAsync();
            if (result.Error != null)
            {
                LastCollectOk = false;
                LastError = result.Error;
                LastStatus = $"采集失败: {result.Error}";
                return;
            }

            var rows = new List<MinuteStatRow>(result.Clients.Count);
            lock (_lock)
            {
                foreach (var c in result.Clients)
                {
                    var key = c.ClientId;
                    var isNew = !_prev.ContainsKey(key);
                    var prev = isNew ? default : _prev[key];
                    if (!isNew)
                    {
                        rows.Add(new MinuteStatRow
                        {
                            ClientId = key,
                            // 呼号优先 client_attrs.callsign（认证时服务端写入，比 username 可靠）
                            Username = c.Callsign ?? c.Username,
                            Uid = c.Uid,
                            Ts = ts,
                            SendOct = Delta(prev.SendOct, c.SendOct, out _),
                            RecvOct = Delta(prev.RecvOct, c.RecvOct, out _),
                            SendMsg = Delta(prev.SendMsg, c.SendMsg, out _),
                            RecvMsg = Delta(prev.RecvMsg, c.RecvMsg, out _),
                            SendPkt = Delta(prev.SendPkt, c.SendPkt, out var rc),
                            RecvPkt = Delta(prev.RecvPkt, c.RecvPkt, out _),
                            IpAddress = c.IpAddress,
                            Reconnect = rc,
                        });
                    }
                    _prev[key] = (c.SendOct, c.RecvOct, c.SendMsg, c.RecvMsg, c.SendPkt, c.RecvPkt);
                }

                // 防膨胀：若历史 clientid 数远超当前在线数，重建只保留在线的（离线设备重连会重新走新客户端逻辑）
                if (_prev.Count > result.Clients.Count * 5 && result.Clients.Count > 0)
                {
                    var cur = new HashSet<string>(result.Clients.Select(c => c.ClientId));
                    foreach (var k in _prev.Keys.Where(k => !cur.Contains(k)).ToList())
                        _prev.Remove(k);
                }
            }

            _db.WriteMinuteStats(rows);
            LastClientCount = result.Clients.Count;

            // ---- 2) 健康采集 ----
            var health = _health.Collect();
            var node = (await _emqx.GetNodesAsync()).FirstOrDefault();
            var (recv, sent) = await _emqx.GetMessageCountsAsync();
            var alarms = await _emqx.GetActiveAlarmsAsync();

            double? msgRate = null;
            if (recv.HasValue && sent.HasValue)
            {
                var total = recv.Value + sent.Value;
                if (_lastMsgTotal is { } lt && total >= lt)
                {
                    var secs = (DateTime.UtcNow - _lastMsgAt).TotalSeconds;
                    if (secs > 0) msgRate = (total - lt) / secs;
                }
                _lastMsgTotal = total;
                _lastMsgAt = DateTime.UtcNow;
            }

            double? emqxMemPct = null;
            if (node is { MemoryUsed: not null } && node.MemoryUsed is { } memUsed && node.MemoryTotal is { } memTotal)
                emqxMemPct = Math.Min(100.0, Math.Max(0.0, 100.0 * memUsed / memTotal));

            _db.WriteHealthSnapshot(new HealthSnapshotRow
            {
                Ts = ts,
                HostCpuPct = health?.CpuPct,
                HostMemUsedPct = health?.MemUsedPct,
                HostDiskUsedPct = health?.DiskUsedPct,
                HostNetRecvKbps = health?.NetRecvKbps,
                HostNetSendKbps = health?.NetSendKbps,
                EmqxNode = node?.Node,
                EmqxCpuPct = node?.Load1,   // EMQX 5.x 无 CPU%，用系统 1 分钟负载（load1）代替
                EmqxMemUsedPct = emqxMemPct,
                EmqxConnections = LastClientCount,
                EmqxMsgRate = msgRate,
                EmqxAlarms = string.IsNullOrEmpty(alarms) ? null : alarms,
            });

            // ---- 3) 过期清理（每 10 分钟）----
            if (now - _lastCleanupAt > TimeSpan.FromMinutes(10))
            {
                _db.CleanupExpired(now);
                _lastCleanupAt = now;
            }

            LastCollectAt = now;
            LastCollectOk = true;
            LastError = null;
            LastStatus = $"采集正常 {now.ToLocalTime():HH:mm:ss}，在线 {LastClientCount}";
        }
        catch (Exception ex)
        {
            LastCollectOk = false;
            LastError = ex.Message;
            LastStatus = $"采集异常: {ex.Message}";
        }
    }

    /// <summary>累计计数器差值；重连（归零）时返回 0 并标记</summary>
    private static long Delta(long prev, long cur, out bool reconnect)
    {
        var d = cur - prev;
        if (d < 0)
        {
            reconnect = true;
            return 0;
        }
        reconnect = false;
        return d;
    }
}
