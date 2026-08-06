using System.Security.Cryptography;

namespace EmqxMonitor;

/// <summary>
/// 主题消息事件接收（规则引擎 Webhook）：
///  - 内存聚合：topic+username+clientid+分钟 为键，累加消息数与字节数
///  - 每 10 秒批量落库 topic_stats（UPSERT 累加）
///  - 内部 token 校验，防止伪造数据灌入
/// 每秒几百~几千条消息事件时，10 秒批量写远优于逐条写。
/// </summary>
public class TopicIngestService : BackgroundService
{
    private readonly Database _db;
    private readonly object _lock = new();
    private readonly Dictionary<(string Topic, string? User, string? Uid, string Cid, string Ts), (long Msg, long Bytes)> _agg = new();

    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(10));

    public TopicIngestService(Database db)
    {
        _db = db;
    }

    public long TotalIngested { get; private set; }
    public DateTime LastIngestAt { get; private set; }

    /// <summary>身份控制开关（默认启用=最高保护）：KICK 时自动拉黑连接身份；关闭后降级为仅记录</summary>
    public volatile bool IdentityControlEnabled = true;

    /// <summary>重置内存状态（完全重置时调用）：清空聚合缓冲与计数</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _agg.Clear();
            TotalIngested = 0;
            LastIngestAt = default;
        }
    }

    /// <summary>内部 token（持久化在 settings，首次生成）</summary>
    public string GetToken(Database db)
    {
        var t = db.GetSetting("ingest_token");
        if (string.IsNullOrEmpty(t))
        {
            t = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            db.SetSetting("ingest_token", t);
        }
        return t;
    }

    /// <summary>接收一条消息事件（webhook 调用），返回是否接受</summary>
    public bool Ingest(string topic, string? username, string? uid, string clientId, long bytes, DateTime now)
    {
        if (string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(clientId) || bytes < 0) return false;
        // 10 秒颗粒度取整（yyyy-MM-dd HH:mm:SS，秒 = 0/10/20/30/40/50）
        var sec = now.Second / 10 * 10;
        var ts = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, sec)
            .ToString("yyyy-MM-dd HH:mm:ss");
        lock (_lock)
        {
            var key = (topic, username, uid, clientId, ts);
            _agg.TryGetValue(key, out var cur);
            _agg[key] = (cur.Msg + 1, cur.Bytes + bytes);
            TotalIngested++;
            LastIngestAt = now;
        }
        return true;
    }

    /// <summary>把内存聚合批量落库并清空</summary>
    public void Flush()
    {
        List<TopicStatRow> rows;
        lock (_lock)
        {
            if (_agg.Count == 0) return;
            rows = _agg.Select(kv => new TopicStatRow
            {
                Topic = kv.Key.Topic,
                Username = kv.Key.User,
                Uid = kv.Key.Uid,
                ClientId = kv.Key.Cid,
                Ts = kv.Key.Ts,
                MsgCount = kv.Value.Msg,
                Bytes = kv.Value.Bytes,
            }).ToList();
            _agg.Clear();
        }
        try
        {
            _db.WriteTopicStats(rows);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TopicIngest] 落库失败: {ex.Message}");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _timer.WaitForNextTickAsync(ct);
                Flush();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        // 退出前把剩余数据落库
        Flush();
    }
}
