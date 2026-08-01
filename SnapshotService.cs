using System.Text.Json;

namespace EmqxMonitor;

/// <summary>
/// 轮询服务：固定 5 秒调 EMQX API → 写快照 → 聚合+异常检测 → 缓存最新结果。
/// /api/snapshot 读取缓存，与轮询节奏一致（前端 5s 刷新与后端 5s 轮询同步）。
/// </summary>
public class SnapshotService : BackgroundService
{
    private readonly EmqxClient _emqx;
    private readonly Database _db;
    private readonly AnomalyDetector _detector = new();
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);
    private volatile bool _configured;

    public DateTime? LastPollAt { get; private set; }
    public bool LastPollOk { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>最近一次聚合结果（/api/snapshot 读取）</summary>
    public SnapshotResult? LatestSnapshot { get; private set; }

    public SnapshotService(EmqxClient emqx, Database db)
    {
        _emqx = emqx;
        _db = db;
    }

    public void SetConfigured(bool configured)
    {
        _configured = configured;
        if (configured) LastError = null;
    }

    /// <summary>最近一次轮询的在线 clientid → 快照信息（用于检测下线）</summary>
    private Dictionary<string, EmqxClientInfo> _lastOnline = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_configured)
            {
                try
                {
                    var result = await _emqx.GetClientsAsync();
                    if (result.Error != null)
                    {
                        LastPollOk = false;
                        LastError = result.Error;
                    }
                    else
                    {
                        var now = DateTime.Now;

                        // 检测下线：上轮在线但本轮消失的客户端，补写 connected=0 快照
                        // （EMQX API 只返回在线客户端，不补写就无法在历史里看到 1→0 翻转）
                        var currentIds = new HashSet<string>(result.Clients.Select(c => c.ClientId));
                        foreach (var (cid, last) in _lastOnline)
                        {
                            if (!currentIds.Contains(cid))
                                _db.WriteOfflineSnapshot(cid, last.Username, last, now);
                        }
                        _lastOnline = result.Clients.ToDictionary(c => c.ClientId);

                        _db.WriteSnapshot(result.Clients, now);
                        _db.AggregateAndClean(now);
                        LatestSnapshot = BuildSnapshot(result.Clients, now);
                        LastPollAt = now;
                        LastPollOk = true;
                        LastError = null;
                        Console.WriteLine($"[poll] {now:HH:mm:ss} clients={result.Clients.Count} users={LatestSnapshot.Users.Count}");
                    }
                }
                catch (Exception ex)
                {
                    LastPollOk = false;
                    LastError = ex.Message;
                }
            }
            await Task.Delay(_interval, stoppingToken);
        }
    }

    /// <summary>聚合：按呼号分组在线客户端 + 异常检测 + 离线呼号（本地缓存见过）</summary>
    private SnapshotResult BuildSnapshot(List<EmqxClientInfo> clients, DateTime now)
    {
        var users = new List<UserAggregate>();

        foreach (var group in clients.GroupBy(c => c.Username ?? "(匿名)"))
        {
            var username = group.Key;
            var list = group.ToList();
            var online = list.Count(c => c.Connected);
            var recvPkt = list.Sum(c => c.RecvPkt);
            var sendPkt = list.Sum(c => c.SendPkt);

            // 速率：该呼号所有客户端累计差值 / 5s（用 detector 的当前速率）
            var (alerts, recvRate, sendRate) =
                _detector.Update(username, online > 0, recvPkt, sendPkt, now);
            Console.WriteLine($"[build] {username} online={online} recvPkt={recvPkt} rate={recvRate:F2}");

            users.Add(new UserAggregate
            {
                Username = username,
                OnlineCount = online,
                ClientCount = list.Count,
                TotalRecvPkt = recvPkt,
                TotalSendPkt = sendPkt,
                TotalRecvMsg = list.Sum(c => c.RecvMsg),
                TotalSendMsg = list.Sum(c => c.SendMsg),
                RateRecvPps = Math.Round(recvRate, 2),
                RateSendPps = Math.Round(sendRate, 2),
                Alerts = alerts,
                Clients = list.Select(c => new ClientBrief
                {
                    ClientId = c.ClientId,
                    Connected = c.Connected,
                    ConnectedAt = c.ConnectedAt,
                    IpAddress = c.IpAddress,
                    RecvPkt = c.RecvPkt,
                    SendPkt = c.SendPkt,
                    RecvMsg = c.RecvMsg,
                    SendMsg = c.SendMsg,
                }).ToList()
            });
        }

        // 离线呼号：本地 SQLite 见过（72h 内），但当前不在线 → 显示上次已知数据
        // 同时喂给 detector（connected=false），保持断连/重连计数连续
        var onlineNames = new HashSet<string>(users.Select(u => u.Username));
        foreach (var known in _db.GetKnownUsernames())
        {
            if (onlineNames.Contains(known)) continue;
            var last = _db.GetLastKnownClient(known);
            if (last != null)
            {
                _detector.Update(known, false, last.RecvPkt, last.SendPkt, now);
            }
            users.Add(new UserAggregate
            {
                Username = known,
                OnlineCount = 0,
                ClientCount = last == null ? 1 : 1,
                TotalRecvPkt = last?.RecvPkt ?? 0,
                TotalSendPkt = last?.SendPkt ?? 0,
                TotalRecvMsg = last?.RecvMsg ?? 0,
                TotalSendMsg = last?.SendMsg ?? 0,
                Alerts = [],
                Offline = true,
                LastSeenAt = last?.SnapshotAt,
                Clients = last == null ? [] : [new ClientBrief
                {
                    ClientId = last.ClientId,
                    Connected = false,
                    ConnectedAt = last.ConnectedAt,
                    IpAddress = last.IpAddress,
                    RecvPkt = last.RecvPkt,
                    SendPkt = last.SendPkt,
                    RecvMsg = last.RecvMsg,
                    SendMsg = last.SendMsg,
                }]
            });
        }

        // 按在线优先 + 呼号排序
        users = users.OrderByDescending(u => u.OnlineCount > 0).ThenBy(u => u.Username).ToList();

        return new SnapshotResult
        {
            SnapshotTime = now.ToString("yyyy-MM-dd HH:mm:ss"),
            OnlineUsers = users.Count(u => u.OnlineCount > 0),
            OfflineUsers = users.Count(u => u.Offline),
            Users = users
        };
    }
}

public class SnapshotResult
{
    public string SnapshotTime { get; set; } = "";
    public int OnlineUsers { get; set; }
    public int OfflineUsers { get; set; }
    public List<UserAggregate> Users { get; set; } = [];
}

public class UserAggregate
{
    public string Username { get; set; } = "";
    public int OnlineCount { get; set; }
    public int ClientCount { get; set; }
    public long TotalRecvPkt { get; set; }
    public long TotalSendPkt { get; set; }
    public long TotalRecvMsg { get; set; }
    public long TotalSendMsg { get; set; }
    public double RateRecvPps { get; set; }
    public double RateSendPps { get; set; }
    public List<string> Alerts { get; set; } = [];
    public bool Offline { get; set; }
    public string? LastSeenAt { get; set; }
    public List<ClientBrief> Clients { get; set; } = [];
}

public class ClientBrief
{
    public string ClientId { get; set; } = "";
    public bool Connected { get; set; }
    public string? ConnectedAt { get; set; }
    public string? IpAddress { get; set; }
    public long RecvPkt { get; set; }
    public long SendPkt { get; set; }
    public long RecvMsg { get; set; }
    public long SendMsg { get; set; }
}
