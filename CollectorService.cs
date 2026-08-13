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
    private readonly ILogger<CollectorService> _log;

    private readonly Lock _lock = new();

    // clientid → 上一轮累计计数器
    private readonly Dictionary<string, (long SendOct, long RecvOct, long SendMsg, long RecvMsg, long SendPkt, long RecvPkt)> _prev = new();

    // uid 重复观察跟踪：uid → (首次发现时间, 连续观察轮数)。连续 3 轮仍重复才处置，
    // 避免 EMQX keepalive 窗口（默认 60s）内设备重连的新旧 clientid 短暂并存被误判为克隆。
    private readonly Dictionary<string, (DateTime FirstSeen, int Count)> _dupUidTrack = new();
    private const int DupUidConfirmCycles = 3;

    private long? _lastMsgTotal;
    private DateTime _lastMsgAt;
    private DateTime _lastCleanupAt = DateTime.MinValue;

    public CollectorService(EmqxClient emqx, Database db, IHostHealthCollector health, ILogger<CollectorService> log)
    {
        _emqx = emqx;
        _db = db;
        _health = health;
        _log = log;
    }

    /// <summary>是否已配置 EMQX 连接（由配置页控制）</summary>
    public volatile bool IsConfigured;

    public DateTime? LastCollectAt { get; private set; }
    public bool LastCollectOk { get; private set; }
    public string? LastError { get; private set; }
    public int LastClientCount { get; private set; }
    public string? LastStatus { get; private set; }

    /// <summary>最近一次采集的在线客户端列表（只读副本；在线页直接读此缓存，不重复打 EMQX）</summary>
    public IReadOnlyList<EmqxClientInfo> LastClients { get; private set; } = [];

    /// <summary>LastClients 的采集时间（本地）</summary>
    public DateTime? LastClientsAt { get; private set; }

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
        LastClients = [];
        LastClientsAt = null;
    }

    private int _collecting; // 防重入：定时循环与手动触发（拉黑后即时刷新）不并发

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

            if (IsConfigured) await CollectAsync();
        }
    }

    /// <summary>立即触发一次采集（拉黑/解封后刷新在线列表缓存用）；正在采集时忽略本次触发</summary>
    public async Task CollectNowAsync()
    {
        if (!IsConfigured || Interlocked.Exchange(ref _collecting, 1) != 0) return;
        try
        {
            await CollectAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _collecting, 0);
        }
    }

    private async Task CollectAsync()
    {
        try
        {
            var now = DateTime.Now; // 服务器本地时间存储（管理员查询/对照投诉时间直观）
            var ts = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).ToString("yyyy-MM-dd HH:mm:00");

            // ---- 1) 客户端增量采集 ----
            var result = await _emqx.GetClientsAsync();
            if (result.Error != null)
            {
                LastCollectOk = false;
                LastError = result.Error;
                LastStatus = $"采集失败: {result.Error}";
                _log.LogWarning("EMQX 采集失败: {Error}", result.Error);
                return;
            }

            var rows = new List<MinuteStatRow>(result.Clients.Count);

            // uid 重复检测 + 自动拉黑
            try
            {
                await DetectAndBanDuplicateUidsAsync(result.Clients, now);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "uid-dup 检测异常");
            }

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
            LastClients = result.Clients; // 缓存在线列表（在线页读取；60s 内新鲜）
            LastClientsAt = now;

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
                EmqxCpuPct = node?.Load1, // EMQX 5.x 无 CPU%，用系统 1 分钟负载（load1）代替
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

    /// <summary>检测 uid 重复并自动拉黑（连续 3 轮确认后永久拉黑全组）</summary>
    private async Task DetectAndBanDuplicateUidsAsync(IReadOnlyList<EmqxClientInfo> clients, DateTime now)
    {
        // 按 uid 分组找重复（uid 为空的不参与——匿名客户端无身份可比）
        var dupGroups = clients.Where(c => !string.IsNullOrEmpty(c.Uid))
            .GroupBy(c => c.Uid!)
            .Where(g => g.Count() > 1)
            .ToList();
        var dupUids = dupGroups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        // 查当前 EMQX 活跃黑名单（读一次，供下面跳过已拉黑）
        var alreadyBanned = _db.QueryActiveBlacklist(now).Select(b => b.Who).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 观测计数 + 筛选出已达确认轮数的组（纯内存操作，锁内完成；await 的拉黑动作放锁外）
        var confirmed = new List<(string Uid, List<string> Callsigns, List<string> ClientIds, int Rounds)>();
        lock (_lock)
        {
            // 清理：不再重复的 uid 撤销跟踪——正常重连恢复后无痕
            foreach (var uid in _dupUidTrack.Keys.Where(k => !dupUids.Contains(k)).ToList())
                _dupUidTrack.Remove(uid);

            foreach (var g in dupGroups)
            {
                var uid = g.Key;
                var callsigns = g.Select(c => (c.Callsign ?? c.Username)?.Trim())
                    .Where(w => !string.IsNullOrEmpty(w))
                    .Select(w => w!)   // Where 已保证非空
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (callsigns.Count == 0) continue;

                // 组内呼号已全部拉黑 → 不再跟踪
                if (callsigns.All(alreadyBanned.Contains)) { _dupUidTrack.Remove(uid); continue; }

                _dupUidTrack.TryGetValue(uid, out var t);
                var count = t.Count + 1;
                var firstSeen = t.Count == 0 ? now : t.FirstSeen;
                _dupUidTrack[uid] = (firstSeen, count);

                if (count < DupUidConfirmCycles)
                {
                    _log.LogInformation("uid-dup uid={Uid} 发现重复连接，观察 {Count}/{Cycles} 轮（{ClientIds}）",
                        uid, count, DupUidConfirmCycles, string.Join(",", g.Select(c => c.ClientId)));
                    continue;
                }

                confirmed.Add((uid, callsigns, g.Select(c => c.ClientId).ToList(), count));
            }

            // 确认处置后清除跟踪，避免后续轮次重复触发
            foreach (var (uid, _, _, _) in confirmed)
                _dupUidTrack.Remove(uid);
        }

        if (confirmed.Count == 0) return;

        // 逐个呼号永久拉黑 + 踢下线 + 留痕（证据带 uid/clientid/持续轮数，便于事后审计）
        const string baseReason = "身份控制: UID 重复登录";
        foreach (var (uid, callsigns, clientIds, rounds) in confirmed)
        {
            var detail = $"uid={uid} 呼号={string.Join("/", callsigns)} clientid={string.Join(",", clientIds)} 持续{rounds}轮";
            foreach (var who in callsigns)
            {
                if (alreadyBanned.Contains(who)) continue;
                var reason = $"{baseReason}（{detail}）";
                var (err, kicked) = await _emqx.BanAsync(who, reason, null); // null = 永久拉黑
                if (err != null)
                {
                    _log.LogWarning("uid-dup 拉黑 {Who} 失败: {Error}", who, err);
                    continue;
                }

                _db.AddBlacklistEvent("ban", "username", who, reason, null, "auto-uid-dup", now);
                _log.LogWarning("uid-dup 已永久拉黑 {Who}（踢出 {Kicked} 个连接，{Detail}）", who, kicked, detail);
            }
        }

        // 被踢客户端下一轮采集自然从 LastClients 消失，无需额外刷新（原 fire-and-forget 会在定时路径并发启动第二次采集，已删除）
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