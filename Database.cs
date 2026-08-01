using Microsoft.Data.Sqlite;

namespace EmqxMonitor;

/// <summary>
/// SQLite 存储层（v2 server 版）：
///  - minute_stats:     每分钟客户端增量（核心表，排行榜数据底座），保留 30 天
///  - health_snapshots: 每分钟健康快照（宿主机 + EMQX），保留 30 天
///  - settings:         KEY-VALUE 配置（EMQX 地址 / API Key / 端口等），持久化
///  - admin_user:       管理员账号（PBKDF2 哈希）
/// 增量必须在客户端在线时算好落库——离线客户端会从 EMQX API 消失，事后无法补算。
/// </summary>
public class Database
{
    private readonly string _connStr;
    private readonly object _lock = new();

    /// <summary>数据保留时长（30 天）</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public Database(string dbPath)
    {
        _connStr = $"Data Source={dbPath}";
        Init();
    }

    private void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS minute_stats (
                clientid    TEXT    NOT NULL,
                username    TEXT,
                ts          TEXT    NOT NULL,   -- 'yyyy-MM-dd HH:mm:00' 分钟级
                send_oct    INTEGER NOT NULL DEFAULT 0,
                recv_oct    INTEGER NOT NULL DEFAULT 0,
                send_msg    INTEGER NOT NULL DEFAULT 0,
                recv_msg    INTEGER NOT NULL DEFAULT 0,
                send_pkt    INTEGER NOT NULL DEFAULT 0,
                recv_pkt    INTEGER NOT NULL DEFAULT 0,
                ip_address  TEXT,
                reconnect   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (clientid, ts)
            );
            CREATE INDEX IF NOT EXISTS idx_min_ts ON minute_stats(ts);
            CREATE INDEX IF NOT EXISTS idx_min_user_ts ON minute_stats(username, ts);

            CREATE TABLE IF NOT EXISTS topic_stats (
                topic     TEXT    NOT NULL,
                username  TEXT,
                clientid  TEXT    NOT NULL,
                ts        TEXT    NOT NULL,   -- 'yyyy-MM-dd HH:mm:00' 分钟级
                msg_count INTEGER NOT NULL DEFAULT 0,
                bytes     INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (topic, clientid, ts)
            );
            CREATE INDEX IF NOT EXISTS idx_topic_ts ON topic_stats(ts);
            CREATE INDEX IF NOT EXISTS idx_topic_user_ts ON topic_stats(topic, username, ts);

            CREATE TABLE IF NOT EXISTS health_snapshots (
                ts                TEXT    PRIMARY KEY,   -- 'yyyy-MM-dd HH:mm:00'
                host_cpu_pct      REAL,
                host_mem_used_pct REAL,
                host_disk_used_pct REAL,
                host_net_recv_kbps REAL,
                host_net_send_kbps REAL,
                emqx_node         TEXT,
                emqx_cpu_pct      REAL,
                emqx_mem_used_pct REAL,
                emqx_connections  INTEGER,
                emqx_msg_rate     REAL,
                emqx_alarms       TEXT
            );

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS admin_user (
                id            INTEGER PRIMARY KEY CHECK (id = 1),
                username      TEXT    NOT NULL,
                password_hash TEXT    NOT NULL,   -- PBKDF2: iterations.salt_b64.hash_b64
                created_at    TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // WAL + 性能（单进程读写，NORMAL 足够安全）
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        return conn;
    }

    // ---------------- settings ----------------

    public string? GetSetting(string key)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings (key, value) VALUES ($k, $v)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------- admin ----------------

    public bool HasAdmin() => GetAdmin() != null;

    public (string Username, string PasswordHash)? GetAdmin()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT username, password_hash FROM admin_user WHERE id = 1";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return (r.GetString(0), r.GetString(1));
        }
    }

    public void CreateAdmin(string username, string passwordHash)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO admin_user (id, username, password_hash, created_at)
                VALUES (1, $u, $h, $t)
                ON CONFLICT(id) DO UPDATE SET username = excluded.username, password_hash = excluded.password_hash
                """;
            cmd.Parameters.AddWithValue("$u", username);
            cmd.Parameters.AddWithValue("$h", passwordHash);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------- minute_stats 写入 ----------------

    /// <summary>写入一批分钟增量行（单事务；同分钟重跑用 INSERT OR REPLACE 覆盖，避免双计）</summary>
    public void WriteMinuteStats(IEnumerable<MinuteStatRow> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO minute_stats
                    (clientid, username, ts, send_oct, recv_oct, send_msg, recv_msg,
                     send_pkt, recv_pkt, ip_address, reconnect)
                VALUES
                    ($cid, $user, $ts, $so, $ro, $sm, $rm, $sp, $rp, $ip, $rc)
                """;
            var p = new Dictionary<string, SqliteParameter>
            {
                ["$cid"] = cmd.Parameters.Add("$cid", SqliteType.Text),
                ["$user"] = cmd.Parameters.Add("$user", SqliteType.Text),
                ["$ts"] = cmd.Parameters.Add("$ts", SqliteType.Text),
                ["$so"] = cmd.Parameters.Add("$so", SqliteType.Integer),
                ["$ro"] = cmd.Parameters.Add("$ro", SqliteType.Integer),
                ["$sm"] = cmd.Parameters.Add("$sm", SqliteType.Integer),
                ["$rm"] = cmd.Parameters.Add("$rm", SqliteType.Integer),
                ["$sp"] = cmd.Parameters.Add("$sp", SqliteType.Integer),
                ["$rp"] = cmd.Parameters.Add("$rp", SqliteType.Integer),
                ["$ip"] = cmd.Parameters.Add("$ip", SqliteType.Text),
                ["$rc"] = cmd.Parameters.Add("$rc", SqliteType.Integer),
            };
            foreach (var r in list)
            {
                p["$cid"].Value = r.ClientId;
                p["$user"].Value = (object?)r.Username ?? DBNull.Value;
                p["$ts"].Value = r.Ts;
                p["$so"].Value = r.SendOct;
                p["$ro"].Value = r.RecvOct;
                p["$sm"].Value = r.SendMsg;
                p["$rm"].Value = r.RecvMsg;
                p["$sp"].Value = r.SendPkt;
                p["$rp"].Value = r.RecvPkt;
                p["$ip"].Value = (object?)r.IpAddress ?? DBNull.Value;
                p["$rc"].Value = r.Reconnect ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---------------- health_snapshots 写入 ----------------

    public void WriteHealthSnapshot(HealthSnapshotRow h)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO health_snapshots
                    (ts, host_cpu_pct, host_mem_used_pct, host_disk_used_pct,
                     host_net_recv_kbps, host_net_send_kbps,
                     emqx_node, emqx_cpu_pct, emqx_mem_used_pct,
                     emqx_connections, emqx_msg_rate, emqx_alarms)
                VALUES
                    ($ts, $hcp, $hmu, $hdu, $hnr, $hns, $en, $ec, $emu, $ecn, $emr, $ea)
                """;
            cmd.Parameters.AddWithValue("$ts", h.Ts);
            cmd.Parameters.AddWithValue("$hcp", (object?)h.HostCpuPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hmu", (object?)h.HostMemUsedPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hdu", (object?)h.HostDiskUsedPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hnr", (object?)h.HostNetRecvKbps ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hns", (object?)h.HostNetSendKbps ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$en", (object?)h.EmqxNode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ec", (object?)h.EmqxCpuPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$emu", (object?)h.EmqxMemUsedPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ecn", (object?)h.EmqxConnections ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$emr", (object?)h.EmqxMsgRate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ea", (object?)h.EmqxAlarms ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------- 排行榜查询 ----------------

    /// <summary>时间段排行榜：按 呼号(或匿名clientid) 聚合，数据量从大到小</summary>
    public List<LeaderboardRow> QueryLeaderboard(string from, string to, string order, int limit = 100)
    {
        var orderCol = order switch
        {
            "msg" => "total_msg",
            "pkt" => "total_pkt",
            _ => "total_oct"
        };
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT COALESCE(username, clientid) AS name,
                       SUM(send_oct + recv_oct) AS total_oct,
                       SUM(send_msg + recv_msg) AS total_msg,
                       SUM(send_pkt + recv_pkt) AS total_pkt,
                       COUNT(DISTINCT clientid) AS device_count,
                       SUM(reconnect)           AS reconnect_count
                FROM minute_stats
                WHERE ts BETWEEN $from AND $to
                GROUP BY name
                ORDER BY {orderCol} DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            cmd.Parameters.AddWithValue("$limit", limit);
            var list = new List<LeaderboardRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new LeaderboardRow
                {
                    Name = r.GetString(0),
                    TotalOct = r.IsDBNull(1) ? 0 : r.GetInt64(1),
                    TotalMsg = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                    TotalPkt = r.IsDBNull(3) ? 0 : r.GetInt64(3),
                    DeviceCount = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                    ReconnectCount = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                });
            }
            return list;
        }
    }

    /// <summary>呼号/客户端明细：该 name 下每个 clientid 的分钟级聚合（排行榜数据底座）</summary>
    public List<ClientDetailRow> QueryClientDetail(string name, string from, string to)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT clientid, ts,
                       send_oct, recv_oct, send_msg, recv_msg, send_pkt, recv_pkt,
                       ip_address, reconnect
                FROM minute_stats
                WHERE ts BETWEEN $from AND $to
                  AND COALESCE(username, clientid) = $name
                ORDER BY clientid, ts
                """;
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            cmd.Parameters.AddWithValue("$name", name);
            var list = new List<ClientDetailRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new ClientDetailRow
                {
                    ClientId = r.GetString(0),
                    Ts = r.GetString(1),
                    SendOct = r.GetInt64(2),
                    RecvOct = r.GetInt64(3),
                    SendMsg = r.GetInt64(4),
                    RecvMsg = r.GetInt64(5),
                    SendPkt = r.GetInt64(6),
                    RecvPkt = r.GetInt64(7),
                    IpAddress = r.IsDBNull(8) ? null : r.GetString(8),
                    Reconnect = r.GetInt64(9) != 0,
                });
            }
            return list;
        }
    }

    // ---------------- 健康查询 ----------------

    public List<HealthSnapshotRow> QueryHealth(string from, string to)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT ts, host_cpu_pct, host_mem_used_pct, host_disk_used_pct,
                       host_net_recv_kbps, host_net_send_kbps,
                       emqx_node, emqx_cpu_pct, emqx_mem_used_pct,
                       emqx_connections, emqx_msg_rate, emqx_alarms
                FROM health_snapshots
                WHERE ts BETWEEN $from AND $to
                ORDER BY ts
                """;
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            var list = new List<HealthSnapshotRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new HealthSnapshotRow
                {
                    Ts = r.GetString(0),
                    HostCpuPct = r.IsDBNull(1) ? null : r.GetDouble(1),
                    HostMemUsedPct = r.IsDBNull(2) ? null : r.GetDouble(2),
                    HostDiskUsedPct = r.IsDBNull(3) ? null : r.GetDouble(3),
                    HostNetRecvKbps = r.IsDBNull(4) ? null : r.GetDouble(4),
                    HostNetSendKbps = r.IsDBNull(5) ? null : r.GetDouble(5),
                    EmqxNode = r.IsDBNull(6) ? null : r.GetString(6),
                    EmqxCpuPct = r.IsDBNull(7) ? null : r.GetDouble(7),
                    EmqxMemUsedPct = r.IsDBNull(8) ? null : r.GetDouble(8),
                    EmqxConnections = r.IsDBNull(9) ? null : r.GetInt64(9),
                    EmqxMsgRate = r.IsDBNull(10) ? null : r.GetDouble(10),
                    EmqxAlarms = r.IsDBNull(11) ? null : r.GetString(11),
                });
            }
            return list;
        }
    }

    // ---------------- topic_stats（规则引擎消息事件聚合） ----------------

    /// <summary>写入一批主题统计行（UPSERT 累加：同一 (topic,clientid,ts) 的聚合批次合并）</summary>
    public void WriteTopicStats(IEnumerable<TopicStatRow> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO topic_stats (topic, username, clientid, ts, msg_count, bytes)
                VALUES ($topic, $user, $cid, $ts, $msg, $bytes)
                ON CONFLICT(topic, clientid, ts) DO UPDATE SET
                    msg_count = msg_count + excluded.msg_count,
                    bytes = bytes + excluded.bytes
                """;
            var p = new Dictionary<string, SqliteParameter>
            {
                ["$topic"] = cmd.Parameters.Add("$topic", SqliteType.Text),
                ["$user"] = cmd.Parameters.Add("$user", SqliteType.Text),
                ["$cid"] = cmd.Parameters.Add("$cid", SqliteType.Text),
                ["$ts"] = cmd.Parameters.Add("$ts", SqliteType.Text),
                ["$msg"] = cmd.Parameters.Add("$msg", SqliteType.Integer),
                ["$bytes"] = cmd.Parameters.Add("$bytes", SqliteType.Integer),
            };
            foreach (var r in list)
            {
                p["$topic"].Value = r.Topic;
                p["$user"].Value = (object?)r.Username ?? DBNull.Value;
                p["$cid"].Value = r.ClientId;
                p["$ts"].Value = r.Ts;
                p["$msg"].Value = r.MsgCount;
                p["$bytes"].Value = r.Bytes;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>主题统计排行榜：按 呼号(或匿名clientid) 聚合，消息数/字节数从大到小</summary>
    public List<TopicLeaderboardRow> QueryTopicLeaderboard(string topic, string from, string to, string order, int limit = 100)
    {
        var orderCol = order == "bytes" ? "total_bytes" : "total_msg";
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT COALESCE(username, clientid) AS name,
                       SUM(msg_count) AS total_msg,
                       SUM(bytes)     AS total_bytes,
                       COUNT(DISTINCT clientid) AS device_count
                FROM topic_stats
                WHERE ts BETWEEN $from AND $to
                  AND (topic = $topic OR topic LIKE $topic || '/%')
                GROUP BY name
                ORDER BY {orderCol} DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            cmd.Parameters.AddWithValue("$topic", topic);
            cmd.Parameters.AddWithValue("$limit", limit);
            var list = new List<TopicLeaderboardRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new TopicLeaderboardRow
                {
                    Name = r.GetString(0),
                    TotalMsg = r.IsDBNull(1) ? 0 : r.GetInt64(1),
                    TotalBytes = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                    DeviceCount = r.IsDBNull(3) ? 0 : r.GetInt64(3),
                });
            }
            return list;
        }
    }

    /// <summary>主题统计明细：某呼号下每个 clientid 的分钟级聚合（含实际 topic）</summary>
    public List<TopicDetailRow> QueryTopicDetail(string topic, string name, string from, string to)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT topic, clientid, ts, msg_count, bytes
                FROM topic_stats
                WHERE ts BETWEEN $from AND $to
                  AND (topic = $topic OR topic LIKE $topic || '/%')
                  AND COALESCE(username, clientid) = $name
                ORDER BY clientid, ts
                """;
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            cmd.Parameters.AddWithValue("$topic", topic);
            cmd.Parameters.AddWithValue("$name", name);
            var list = new List<TopicDetailRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new TopicDetailRow
                {
                    Topic = r.GetString(0),
                    ClientId = r.GetString(1),
                    Ts = r.GetString(2),
                    MsgCount = r.GetInt64(3),
                    Bytes = r.GetInt64(4),
                });
            }
            return list;
        }
    }

    /// <summary>主题时间轴：按桶聚合（1m 原始分钟 / 5m / 1h），含该桶内去重发言人数 + 每桶发包 Top8 呼号明细</summary>
    public List<TopicTimelineRow> QueryTopicTimeline(string topic, string from, string to, string bucket)
    {
        // 桶表达式（ts 格式 yyyy-MM-dd HH:mm:00）
        var bucketExpr = bucket switch
        {
            "5m" => "substr(ts,1,14) || printf('%02d', CAST(substr(ts,15,2) AS INTEGER)/5*5) || ':00'",
            "1h" => "substr(ts,1,13) || ':00:00'",
            _ => "ts"
        };
        lock (_lock)
        {
            using var conn = Open();

            // 1) 每桶总量 + 去重人数
            var totals = new Dictionary<string, (long Msg, long Bytes, long Users)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT {bucketExpr} AS bucket_ts,
                           SUM(msg_count) AS total_msg,
                           SUM(bytes)     AS total_bytes,
                           COUNT(DISTINCT COALESCE(username, clientid)) AS user_count
                    FROM topic_stats
                    WHERE ts BETWEEN $from AND $to
                      AND (topic = $topic OR topic LIKE $topic || '/%')
                    GROUP BY bucket_ts
                    ORDER BY bucket_ts
                    """;
                cmd.Parameters.AddWithValue("$from", from);
                cmd.Parameters.AddWithValue("$to", to);
                cmd.Parameters.AddWithValue("$topic", topic);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    totals[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
            }

            // 2) 每桶发包 Top8 呼号明细（窗口函数，SQL 层截断行数）
            var topUsers = new Dictionary<string, List<TopicUserStat>>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT * FROM (
                        SELECT {bucketExpr} AS bucket_ts,
                               COALESCE(username, clientid) AS name,
                               SUM(msg_count) AS msg,
                               ROW_NUMBER() OVER (
                                   PARTITION BY {bucketExpr}
                                   ORDER BY SUM(msg_count) DESC, COALESCE(username, clientid)
                               ) AS rn
                        FROM topic_stats
                        WHERE ts BETWEEN $from AND $to
                          AND (topic = $topic OR topic LIKE $topic || '/%')
                        GROUP BY {bucketExpr}, COALESCE(username, clientid)
                    ) WHERE rn <= 8
                    ORDER BY bucket_ts, rn
                    """;
                cmd.Parameters.AddWithValue("$from", from);
                cmd.Parameters.AddWithValue("$to", to);
                cmd.Parameters.AddWithValue("$topic", topic);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var ts = r.GetString(0);
                    if (!topUsers.TryGetValue(ts, out var list))
                        topUsers[ts] = list = new List<TopicUserStat>();
                    list.Add(new TopicUserStat { Name = r.GetString(1), Msg = r.GetInt64(2) });
                }
            }

            var result = new List<TopicTimelineRow>();
            foreach (var (ts, t) in totals)
            {
                result.Add(new TopicTimelineRow
                {
                    Ts = ts,
                    MsgCount = t.Msg,
                    Bytes = t.Bytes,
                    UserCount = t.Users,
                    TopUsers = topUsers.TryGetValue(ts, out var u) ? u : [],
                });
            }
            return result;
        }
    }

    // ---------------- 过期清理 ----------------

    /// <summary>删除 30 天前的增量与健康数据（分批删除，避免长事务锁库）</summary>
    public void CleanupExpired(DateTime now)
    {
        var cutoff = now.Add(-Retention).ToString("yyyy-MM-dd HH:mm:00");
        lock (_lock)
        {
            using var conn = Open();
            foreach (var table in new[] { "minute_stats", "health_snapshots", "topic_stats" })
            {
                // 分批删：每批 20000 行，直到删不动
                // 注意：SQLite 默认不支持 DELETE ... LIMIT（语法错误），必须用 rowid 子查询分批
                for (var i = 0; i < 200; i++)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE ts < $cutoff LIMIT 20000)";
                    cmd.Parameters.AddWithValue("$cutoff", cutoff);
                    var affected = cmd.ExecuteNonQuery();
                    if (affected == 0) break;
                }
            }
        }
    }
}

/// <summary>主题时间轴行（桶级：总量 + Top 呼号明细）</summary>
public class TopicTimelineRow
{
    public string Ts { get; init; } = "";
    public long MsgCount { get; init; }
    public long Bytes { get; init; }
    public long UserCount { get; init; }
    public List<TopicUserStat> TopUsers { get; init; } = [];
}

/// <summary>时间轴桶内单个呼号的发包量</summary>
public class TopicUserStat
{
    public string Name { get; init; } = "";
    public long Msg { get; init; }
}

/// <summary>主题统计行（规则引擎消息事件按 topic+clientid+分钟聚合）</summary>
public class TopicStatRow
{
    public required string Topic { get; init; }
    public string? Username { get; init; }
    public required string ClientId { get; init; }
    public required string Ts { get; init; }
    public long MsgCount { get; init; }
    public long Bytes { get; init; }
}

/// <summary>主题统计排行榜行</summary>
public class TopicLeaderboardRow
{
    public string Name { get; init; } = "";
    public long TotalMsg { get; init; }
    public long TotalBytes { get; init; }
    public long DeviceCount { get; init; }
}

/// <summary>主题统计明细行</summary>
public class TopicDetailRow
{
    public string Topic { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string Ts { get; init; } = "";
    public long MsgCount { get; init; }
    public long Bytes { get; init; }
}

/// <summary>分钟增量行</summary>
public class MinuteStatRow
{
    public required string ClientId { get; init; }
    public string? Username { get; init; }
    public required string Ts { get; init; }          // 'yyyy-MM-dd HH:mm:00'
    public long SendOct { get; init; }
    public long RecvOct { get; init; }
    public long SendMsg { get; init; }
    public long RecvMsg { get; init; }
    public long SendPkt { get; init; }
    public long RecvPkt { get; init; }
    public string? IpAddress { get; init; }
    public bool Reconnect { get; init; }
}

/// <summary>健康快照行</summary>
public class HealthSnapshotRow
{
    public required string Ts { get; init; }
    public double? HostCpuPct { get; init; }
    public double? HostMemUsedPct { get; init; }
    public double? HostDiskUsedPct { get; init; }
    public double? HostNetRecvKbps { get; init; }
    public double? HostNetSendKbps { get; init; }
    public string? EmqxNode { get; init; }
    public double? EmqxCpuPct { get; init; }
    public double? EmqxMemUsedPct { get; init; }
    public long? EmqxConnections { get; init; }
    public double? EmqxMsgRate { get; init; }
    public string? EmqxAlarms { get; init; }
}

/// <summary>排行榜行</summary>
public class LeaderboardRow
{
    public string Name { get; init; } = "";
    public long TotalOct { get; init; }
    public long TotalMsg { get; init; }
    public long TotalPkt { get; init; }
    public long DeviceCount { get; init; }
    public long ReconnectCount { get; init; }
}

/// <summary>呼号明细行</summary>
public class ClientDetailRow
{
    public string ClientId { get; init; } = "";
    public string Ts { get; init; } = "";
    public long SendOct { get; init; }
    public long RecvOct { get; init; }
    public long SendMsg { get; init; }
    public long RecvMsg { get; init; }
    public long SendPkt { get; init; }
    public long RecvPkt { get; init; }
    public string? IpAddress { get; init; }
    public bool Reconnect { get; init; }
}
