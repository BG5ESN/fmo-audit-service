using Microsoft.Data.Sqlite;

namespace EmqxMonitor;

/// <summary>
/// SQLite 存储：两级设计
///  - client_snapshots: 5s 原始快照（累计计数器），保留 1 小时，供实时/异常/session 分析
///  - client_minutes:   分钟聚合增量（该分钟实际收发包数），保留 72 小时，供趋势图/历史
/// 原因：recv_pkt 等是累计计数器，分钟聚合必须用"末值-初值"算增量，均值会得到错误曲线。
/// </summary>
public class Database
{
    private readonly string _connStr;
    private readonly object _lock = new();

    /// <summary>原始快照保留时长</summary>
    public static readonly TimeSpan RawRetention = TimeSpan.FromHours(1);
    /// <summary>分钟聚合保留时长</summary>
    public static readonly TimeSpan MinuteRetention = TimeSpan.FromHours(72);

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
            CREATE TABLE IF NOT EXISTS client_snapshots (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                clientid     TEXT    NOT NULL,
                username     TEXT,
                connected    INTEGER NOT NULL,
                connected_at TEXT,
                ip_address   TEXT,
                recv_pkt     INTEGER,
                send_pkt     INTEGER,
                recv_msg     INTEGER,
                send_msg     INTEGER,
                recv_oct     INTEGER,
                send_oct     INTEGER,
                node         TEXT,
                snapshot_at  TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_snap_user_time ON client_snapshots(username, snapshot_at);
            CREATE INDEX IF NOT EXISTS idx_snap_time ON client_snapshots(snapshot_at);
            CREATE INDEX IF NOT EXISTS idx_snap_client_time ON client_snapshots(clientid, snapshot_at);

            CREATE TABLE IF NOT EXISTS client_minutes (
                username        TEXT NOT NULL,
                clientid        TEXT NOT NULL,
                minute          TEXT NOT NULL,   -- 'yyyy-MM-dd HH:mm'
                recv_pkt_delta  INTEGER NOT NULL DEFAULT 0,
                send_pkt_delta  INTEGER NOT NULL DEFAULT 0,
                recv_msg_delta  INTEGER NOT NULL DEFAULT 0,
                send_msg_delta  INTEGER NOT NULL DEFAULT 0,
                recv_oct_delta  INTEGER NOT NULL DEFAULT 0,
                send_oct_delta  INTEGER NOT NULL DEFAULT 0,
                connected_secs  INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (username, clientid, minute)
            );
            CREATE INDEX IF NOT EXISTS idx_min_user_time ON client_minutes(username, minute);
            CREATE INDEX IF NOT EXISTS idx_min_time ON client_minutes(minute);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        return conn;
    }

    /// <summary>写入一条离线快照（客户端断开时补写，保留上次统计值）</summary>
    public void WriteOfflineSnapshot(string clientId, string? username, EmqxClientInfo last, DateTime snapshotAt)
    {
        var ts = snapshotAt.ToString("yyyy-MM-dd HH:mm:ss.fff");
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO client_snapshots
                    (clientid, username, connected, connected_at, ip_address,
                     recv_pkt, send_pkt, recv_msg, send_msg, recv_oct, send_oct,
                     node, snapshot_at)
                VALUES
                    ($cid, $user, 0, $connAt, $ip,
                     $rp, $sp, $rm, $sm, $ro, $so,
                     $node, $ts)
                """;
            cmd.Parameters.AddWithValue("$cid", clientId);
            cmd.Parameters.AddWithValue("$user", (object?)username ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$connAt", (object?)last.ConnectedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ip", (object?)last.IpAddress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rp", last.RecvPkt);
            cmd.Parameters.AddWithValue("$sp", last.SendPkt);
            cmd.Parameters.AddWithValue("$rm", last.RecvMsg);
            cmd.Parameters.AddWithValue("$sm", last.SendMsg);
            cmd.Parameters.AddWithValue("$ro", last.RecvOct);
            cmd.Parameters.AddWithValue("$so", last.SendOct);
            cmd.Parameters.AddWithValue("$node", (object?)last.Node ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>写入一批客户端快照（单事务）</summary>
    public void WriteSnapshot(IEnumerable<EmqxClientInfo> clients, DateTime snapshotAt)
    {
        var ts = snapshotAt.ToString("yyyy-MM-dd HH:mm:ss.fff");
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO client_snapshots
                    (clientid, username, connected, connected_at, ip_address,
                     recv_pkt, send_pkt, recv_msg, send_msg, recv_oct, send_oct,
                     node, snapshot_at)
                VALUES
                    ($cid, $user, $conn, $connAt, $ip,
                     $rp, $sp, $rm, $sm, $ro, $so,
                     $node, $ts)
                """;
            var p = new Dictionary<string, SqliteParameter>
            {
                ["$cid"] = cmd.Parameters.Add("$cid", SqliteType.Text),
                ["$user"] = cmd.Parameters.Add("$user", SqliteType.Text),
                ["$conn"] = cmd.Parameters.Add("$conn", SqliteType.Integer),
                ["$connAt"] = cmd.Parameters.Add("$connAt", SqliteType.Text),
                ["$ip"] = cmd.Parameters.Add("$ip", SqliteType.Text),
                ["$rp"] = cmd.Parameters.Add("$rp", SqliteType.Integer),
                ["$sp"] = cmd.Parameters.Add("$sp", SqliteType.Integer),
                ["$rm"] = cmd.Parameters.Add("$rm", SqliteType.Integer),
                ["$sm"] = cmd.Parameters.Add("$sm", SqliteType.Integer),
                ["$ro"] = cmd.Parameters.Add("$ro", SqliteType.Integer),
                ["$so"] = cmd.Parameters.Add("$so", SqliteType.Integer),
                ["$node"] = cmd.Parameters.Add("$node", SqliteType.Text),
                ["$ts"] = cmd.Parameters.Add("$ts", SqliteType.Text),
            };

            foreach (var c in clients)
            {
                p["$cid"].Value = c.ClientId;
                p["$user"].Value = (object?)c.Username ?? DBNull.Value;
                p["$conn"].Value = c.Connected ? 1 : 0;
                p["$connAt"].Value = (object?)c.ConnectedAt ?? DBNull.Value;
                p["$ip"].Value = (object?)c.IpAddress ?? DBNull.Value;
                p["$rp"].Value = c.RecvPkt;
                p["$sp"].Value = c.SendPkt;
                p["$rm"].Value = c.RecvMsg;
                p["$sm"].Value = c.SendMsg;
                p["$ro"].Value = c.RecvOct;
                p["$so"].Value = c.SendOct;
                p["$node"].Value = (object?)c.Node ?? DBNull.Value;
                p["$ts"].Value = ts;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// 分钟聚合 + 过期清理（每次轮询后轻量执行，避免定时器复杂化）：
    ///  1. 把超过 RawRetention 的原始快照聚合成分钟增量（末值-初值）
    ///  2. 删除已聚合的原始快照
    ///  3. 删除超过 MinuteRetention 的分钟数据
    /// </summary>
    public void AggregateAndClean(DateTime now)
    {
        var rawCutoff = now.Add(-RawRetention).ToString("yyyy-MM-dd HH:mm:ss.fff");
        var minCutoff = now.Add(-MinuteRetention).ToString("yyyy-MM-dd HH:mm:ss");
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            // 1) 找出所有需要聚合的原始行（按 clientid, snapshot_at 排序）
            var rows = new List<(string clientid, string username, string ts, long rp, long sp, long rm, long sm, long ro, long so, int connected)>();
            using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    SELECT clientid, username, snapshot_at, recv_pkt, send_pkt, recv_msg, send_msg, recv_oct, send_oct, connected
                    FROM client_snapshots WHERE snapshot_at < $cutoff
                    ORDER BY clientid, snapshot_at
                    """;
                q.Parameters.AddWithValue("$cutoff", rawCutoff);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    rows.Add((
                        r.GetString(0),
                        r.IsDBNull(1) ? "" : r.GetString(1),
                        r.GetString(2),
                        r.IsDBNull(3) ? 0 : r.GetInt64(3),
                        r.IsDBNull(4) ? 0 : r.GetInt64(4),
                        r.IsDBNull(5) ? 0 : r.GetInt64(5),
                        r.IsDBNull(6) ? 0 : r.GetInt64(6),
                        r.IsDBNull(7) ? 0 : r.GetInt64(7),
                        r.IsDBNull(8) ? 0 : r.GetInt64(8),
                        r.GetInt32(9)));
                }
            }

            // 2) 按 clientid 分桶，逐分钟累加增量（末值-初值）
            //    每个 clientid 维护"上一条记录的值"，delta 计入该记录所在分钟
            var minuteBuckets = new Dictionary<(string user, string cid, string minute),
                (long rp, long sp, long rm, long sm, long ro, long so, int secs)>();
            long? prevRp = null, prevSp = null, prevRm = null, prevSm = null, prevRo = null, prevSo = null;
            string? prevCid = null;

            foreach (var row in rows)
            {
                var minute = row.ts[..16]; // yyyy-MM-dd HH:mm
                var key = (row.username, row.clientid, minute);
                if (!minuteBuckets.TryGetValue(key, out var b))
                    b = (0, 0, 0, 0, 0, 0, 0);
                // 在线秒数：每行快照代表 5s 间隔，组内每行都计入（含第一行）
                b.secs += row.connected * 5;
                if (prevCid == row.clientid)
                {
                    // 同客户端：delta = 当前值 - 上一条值
                    b.rp += Math.Max(0, row.rp - (prevRp ?? row.rp));
                    b.sp += Math.Max(0, row.sp - (prevSp ?? row.sp));
                    b.rm += Math.Max(0, row.rm - (prevRm ?? row.rm));
                    b.sm += Math.Max(0, row.sm - (prevSm ?? row.sm));
                    b.ro += Math.Max(0, row.ro - (prevRo ?? row.ro));
                    b.so += Math.Max(0, row.so - (prevSo ?? row.so));
                }
                minuteBuckets[key] = b;
                prevCid = row.clientid;
                prevRp = row.rp; prevSp = row.sp; prevRm = row.rm; prevSm = row.sm; prevRo = row.ro; prevSo = row.so;
            }

            // 3) UPSERT 分钟数据
            using (var up = conn.CreateCommand())
            {
                up.Transaction = tx;
                up.CommandText = """
                    INSERT INTO client_minutes
                        (username, clientid, minute, recv_pkt_delta, send_pkt_delta,
                         recv_msg_delta, send_msg_delta, recv_oct_delta, send_oct_delta, connected_secs)
                    VALUES ($user, $cid, $min, $rp, $sp, $rm, $sm, $ro, $so, $secs)
                    ON CONFLICT(username, clientid, minute) DO UPDATE SET
                        recv_pkt_delta = recv_pkt_delta + excluded.recv_pkt_delta,
                        send_pkt_delta = send_pkt_delta + excluded.send_pkt_delta,
                        recv_msg_delta = recv_msg_delta + excluded.recv_msg_delta,
                        send_msg_delta = send_msg_delta + excluded.send_msg_delta,
                        recv_oct_delta = recv_oct_delta + excluded.recv_oct_delta,
                        send_oct_delta = send_oct_delta + excluded.send_oct_delta,
                        connected_secs = connected_secs + excluded.connected_secs
                    """;
                var pp = new Dictionary<string, SqliteParameter>
                {
                    ["$user"] = up.Parameters.Add("$user", SqliteType.Text),
                    ["$cid"] = up.Parameters.Add("$cid", SqliteType.Text),
                    ["$min"] = up.Parameters.Add("$min", SqliteType.Text),
                    ["$rp"] = up.Parameters.Add("$rp", SqliteType.Integer),
                    ["$sp"] = up.Parameters.Add("$sp", SqliteType.Integer),
                    ["$rm"] = up.Parameters.Add("$rm", SqliteType.Integer),
                    ["$sm"] = up.Parameters.Add("$sm", SqliteType.Integer),
                    ["$ro"] = up.Parameters.Add("$ro", SqliteType.Integer),
                    ["$so"] = up.Parameters.Add("$so", SqliteType.Integer),
                    ["$secs"] = up.Parameters.Add("$secs", SqliteType.Integer),
                };
                foreach (var (key, b) in minuteBuckets)
                {
                    pp["$user"].Value = key.user;
                    pp["$cid"].Value = key.cid;
                    pp["$min"].Value = key.minute;
                    pp["$rp"].Value = b.rp;
                    pp["$sp"].Value = b.sp;
                    pp["$rm"].Value = b.rm;
                    pp["$sm"].Value = b.sm;
                    pp["$ro"].Value = b.ro;
                    pp["$so"].Value = b.so;
                    pp["$secs"].Value = b.secs;
                    up.ExecuteNonQuery();
                }
            }

            // 4) 删除已聚合的原始快照
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM client_snapshots WHERE snapshot_at < $cutoff";
                del.Parameters.AddWithValue("$cutoff", rawCutoff);
                del.ExecuteNonQuery();
            }

            // 5) 删除过期分钟数据
            using (var del2 = conn.CreateCommand())
            {
                del2.Transaction = tx;
                del2.CommandText = "DELETE FROM client_minutes WHERE minute < $cutoff";
                del2.Parameters.AddWithValue("$cutoff", minCutoff);
                del2.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>查询某呼号最近 N 分钟的趋势（原始 5s 粒度，未聚合部分）</summary>
    public List<TrendPoint> QueryTrendRaw(string username, DateTime from, DateTime to)
    {
        var rows = new List<TrendPoint>();
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT clientid, snapshot_at, recv_pkt, send_pkt, recv_msg, send_msg, recv_oct, send_oct
                FROM client_snapshots
                WHERE username = $user AND snapshot_at >= $from AND snapshot_at <= $to
                ORDER BY clientid, snapshot_at
                """;
            cmd.Parameters.AddWithValue("$user", username);
            cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new TrendPoint
                {
                    ClientId = r.GetString(0),
                    Time = r.GetString(1),
                    RecvPkt = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                    SendPkt = r.IsDBNull(3) ? 0 : r.GetInt64(3),
                    RecvMsg = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                    SendMsg = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                    RecvOct = r.IsDBNull(6) ? 0 : r.GetInt64(6),
                    SendOct = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                });
            }
        }
        return rows;
    }

    /// <summary>72 小时内出现过的所有呼号（在线+离线展示用）</summary>
    public List<string> GetKnownUsernames()
    {
        var result = new List<string>();
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT username FROM (
                    SELECT username FROM client_snapshots
                    UNION
                    SELECT username FROM client_minutes
                ) WHERE username IS NOT NULL AND username != ''
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetString(0));
        }
        return result;
    }

    /// <summary>某呼号最近一条已知快照（离线展示"上次已知数据"）</summary>
    public LastKnownClient? GetLastKnownClient(string username)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT clientid, username, connected, connected_at, ip_address,
                       recv_pkt, send_pkt, recv_msg, send_msg, snapshot_at
                FROM client_snapshots
                WHERE username = $user
                ORDER BY snapshot_at DESC LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$user", username);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new LastKnownClient
            {
                ClientId = r.GetString(0),
                Connected = r.GetInt32(2) == 1,
                ConnectedAt = r.IsDBNull(3) ? null : r.GetString(3),
                IpAddress = r.IsDBNull(4) ? null : r.GetString(4),
                RecvPkt = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                SendPkt = r.IsDBNull(6) ? 0 : r.GetInt64(6),
                RecvMsg = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                SendMsg = r.IsDBNull(8) ? 0 : r.GetInt64(8),
                SnapshotAt = r.GetString(9),
            };
        }
    }

    /// <summary>
    /// 查询某呼号的历史上线记录（session 列表）。
    /// 1h 内：原始快照按 connected 0→1/1→0 精确切分（5s 粒度）
    /// 更早：分钟表按 connected_secs &gt; 0 的连续分钟合并（分钟粒度）
    /// </summary>
    public List<SessionInfo> QuerySessions(string username, DateTime from, DateTime to)
    {
        var sessions = new List<SessionInfo>();
        lock (_lock)
        {
            // 1) 原始快照切分（from 起 1h 内，或 to 之前）
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT snapshot_at, connected FROM client_snapshots
                    WHERE username = $user AND snapshot_at >= $from AND snapshot_at <= $to
                    ORDER BY snapshot_at
                    """;
                cmd.Parameters.AddWithValue("$user", username);
                cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                using var r = cmd.ExecuteReader();
                DateTime? openAt = null;
                while (r.Read())
                {
                    var ts = DateTime.ParseExact(r.GetString(0), "yyyy-MM-dd HH:mm:ss.fff", null);
                    var connected = r.GetInt32(1) == 1;
                    if (connected && openAt == null) openAt = ts;
                    else if (!connected && openAt != null)
                    {
                        sessions.Add(new SessionInfo { Start = openAt.Value, End = ts });
                        openAt = null;
                    }
                }
                if (openAt != null) sessions.Add(new SessionInfo { Start = openAt.Value, End = null }); // 仍在线
            }

            // 2) 分钟表合并（仅 from 到 to 且超出原始表覆盖范围的部分）
            //    分钟表只存在线分钟（connected_secs>0 才写入），离线分钟无行，
            //    所以用"下一行间隔 >1 分钟"判断 session 闭合
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT minute, connected_secs FROM client_minutes
                    WHERE username = $user AND minute >= $from AND minute <= $to
                    ORDER BY minute
                    """;
                cmd.Parameters.AddWithValue("$user", username);
                cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd HH:mm"));
                cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd HH:mm"));
                using var r = cmd.ExecuteReader();
                DateTime? openAt = null;
                DateTime? prevMinute = null;
                while (r.Read())
                {
                    var minute = DateTime.ParseExact(r.GetString(0), "yyyy-MM-dd HH:mm", null);
                    // 时间间隙 >1 分钟：中间断线，闭合当前 session
                    if (openAt != null && prevMinute != null && (minute - prevMinute.Value) > TimeSpan.FromMinutes(1))
                    {
                        sessions.Add(new SessionInfo { Start = openAt.Value, End = prevMinute.Value.AddMinutes(1) });
                        openAt = null;
                    }
                    if (openAt == null) openAt = minute;
                    prevMinute = minute;
                }
                if (openAt != null && prevMinute != null)
                    sessions.Add(new SessionInfo { Start = openAt.Value, End = prevMinute.Value.AddMinutes(1) });
            }
        }

        // 合并两个来源的 session，按开始时间排序，去重叠
        var merged = new List<SessionInfo>();
        foreach (var s in sessions.OrderBy(s => s.Start))
        {
            if (merged.Count == 0 || s.Start > (merged[^1].End ?? DateTime.MaxValue))
            {
                merged.Add(s);
            }
            else
            {
                // 与上一条重叠或相接：合并
                var last = merged[^1];
                if (s.End != null && (last.End == null || s.End > last.End))
                    last.End = s.End;
            }
        }
        return merged;
    }

    /// <summary>查询某呼号的分钟聚合趋势（历史部分）</summary>
    public List<MinutePoint> QueryTrendMinute(string username, DateTime from, DateTime to)
    {
        var rows = new List<MinutePoint>();
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT minute, SUM(recv_pkt_delta), SUM(send_pkt_delta), SUM(recv_msg_delta),
                       SUM(send_msg_delta), SUM(recv_oct_delta), SUM(send_oct_delta), MAX(connected_secs)
                FROM client_minutes
                WHERE username = $user AND minute >= $from AND minute <= $to
                GROUP BY minute ORDER BY minute
                """;
            cmd.Parameters.AddWithValue("$user", username);
            cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd HH:mm"));
            cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd HH:mm"));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new MinutePoint
                {
                    Minute = r.GetString(0),
                    RecvPkt = r.IsDBNull(1) ? 0 : r.GetInt64(1),
                    SendPkt = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                    RecvMsg = r.IsDBNull(3) ? 0 : r.GetInt64(3),
                    SendMsg = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                    RecvOct = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                    SendOct = r.IsDBNull(6) ? 0 : r.GetInt64(6),
                    ConnectedSecs = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                });
            }
        }
        return rows;
    }
}

public class TrendPoint
{
    public string ClientId { get; set; } = "";
    public string Time { get; set; } = "";
    public long RecvPkt { get; set; }
    public long SendPkt { get; set; }
    public long RecvMsg { get; set; }
    public long SendMsg { get; set; }
    public long RecvOct { get; set; }
    public long SendOct { get; set; }
}

public class MinutePoint
{
    public string Minute { get; set; } = "";
    public long RecvPkt { get; set; }
    public long SendPkt { get; set; }
    public long RecvMsg { get; set; }
    public long SendMsg { get; set; }
    public long RecvOct { get; set; }
    public long SendOct { get; set; }
    public int ConnectedSecs { get; set; }
}

public class LastKnownClient
{
    public string ClientId { get; set; } = "";
    public bool Connected { get; set; }
    public string? ConnectedAt { get; set; }
    public string? IpAddress { get; set; }
    public long RecvPkt { get; set; }
    public long SendPkt { get; set; }
    public long RecvMsg { get; set; }
    public long SendMsg { get; set; }
    public string SnapshotAt { get; set; } = "";
}

public class SessionInfo
{
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }   // null = 仍在线

    public double DurationSeconds => End == null
        ? (DateTime.Now - Start).TotalSeconds
        : (End.Value - Start).TotalSeconds;
}
