namespace EmqxMonitor;

/// <summary>
/// 异常检测：全部在后端计算，前端只管渲染醒目样式（NF6）。
/// 规则（文档 196-219 行）：
///   R1 速率异常: 当前收/发速率 > 该呼号过去 10 分钟平均速率 × 3
///   R2 频繁断连: 5 分钟内同一呼号断开又重连 > 3 次
///   R3 长期无数据: 在线 > 30 分钟，最近 10 分钟无任何包变化（疑似死连接）
/// 注意方向：设备上报数据 = EMQX recv_pkt 增长，所以"用户异常发包"检测 recv_pkt 方向；
///           send_pkt 方向（下行）同样检测，任一方向超 3 倍即告警。
/// </summary>
public class AnomalyDetector
{
    private const double RateThreshold = 3.0;
    private static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NoDataOnlineMin = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NoDataIdleMin = TimeSpan.FromMinutes(10);

    /// <summary>速率基线窗口：10 分钟（5s × 120 采样）</summary>
    private const int RateWindowSize = 120;

    private readonly Dictionary<string, UserState> _users = new();

    private class UserState
    {
        public Queue<double> RecvRates = new();
        public Queue<double> SendRates = new();
        public Queue<DateTime> Reconnects = new();
        public long LastRecvPkt = -1;
        public long LastSendPkt = -1;
        public bool PrevConnected;
        public DateTime? FirstOnlineAt;
        public DateTime LastActivityAt = DateTime.MinValue;
        public long LastTotal;
        public double LastRecvRate;
        public double LastSendRate;
        public bool RateAnomalyActive;   // 去抖：触发后保持，直到速率回落到基线 2 倍以下
        public double LastBaseline;
        public DateTime LastUpdateAt = DateTime.MinValue;  // 实际轮询时间（算真实间隔）
    }

    /// <summary>
    /// 每轮轮询调用一次：更新呼号状态，返回该呼号当前的告警列表和速率（包/秒）。
    /// </summary>
    public (List<string> Alerts, double RecvRate, double SendRate) Update(
        string username, bool connected, long recvPkt, long sendPkt, DateTime now)
    {
        if (!_users.TryGetValue(username, out var st))
        {
            st = new UserState();
            _users[username] = st;
        }

        // ---- 速率（前后快照差值 / 实际间隔秒数）----
        if (st.LastRecvPkt >= 0)
        {
            // 用实际轮询间隔而非固定 5s：EMQX 调用偶发变慢时，间隔会拉长，
            // 固定 dt 会把速率高估（如 15s 增量 ÷ 5 = 3 倍假速率）
            var dt = st.LastUpdateAt == DateTime.MinValue
                ? 5.0
                : Math.Clamp((now - st.LastUpdateAt).TotalSeconds, 1.0, 60.0);
            var rp = Math.Max(0, recvPkt - st.LastRecvPkt) / dt;
            var sp = Math.Max(0, sendPkt - st.LastSendPkt) / dt;
            Push(st.RecvRates, rp, RateWindowSize);
            Push(st.SendRates, sp, RateWindowSize);
            st.LastRecvRate = rp;
            st.LastSendRate = sp;
        }
        st.LastRecvPkt = recvPkt;
        st.LastSendPkt = sendPkt;
        st.LastUpdateAt = now;

        // ---- 断连记录：0→1 翻转计一次重连 ----
        if (!st.PrevConnected && connected)
        {
            st.Reconnects.Enqueue(now);
            while (st.Reconnects.Count > 0 && now - st.Reconnects.Peek() > FlapWindow)
                st.Reconnects.Dequeue();
            if (st.FirstOnlineAt == null) st.FirstOnlineAt = now;
        }
        st.PrevConnected = connected;

        // ---- 活动时间：累计包数变化即视为有数据 ----
        var total = recvPkt + sendPkt;
        if (total != st.LastTotal) { st.LastActivityAt = now; st.LastTotal = total; }

        // ---- 规则判定 ----
        var alerts = new List<string>();

        // R1 速率异常：任一方向 > 基线 P25 × 3（基线不足 5 个采样时不判，避免启动误报）
        // 基线用 P25 低分位数而非均值/中位数：
        //   - 均值/中位数会被突变值本身污染（持续突变时阈值被抬高，告警失效）
        //   - P25 在正常期占窗口多数时保持稳定（突变占 <75% 时不受影响）
        // 去抖：触发后保持告警，直到速率回落到基线 2 倍以下（避免闪烁）
        // 注意：0 速率采样（客户端批量发包时的空窗期）不计入基线，
        //       否则 P25 被 0 拉低后 baseline=0，突变永远检测不到。
        if (st.RecvRates.Count >= 5 || st.SendRates.Count >= 5)
        {
            var recvBase = st.RecvRates.Count >= 5 ? PercentileNonZero(st.RecvRates, 0.25) : 0;
            var sendBase = st.SendRates.Count >= 5 ? PercentileNonZero(st.SendRates, 0.25) : 0;
            var maxRate = Math.Max(st.LastRecvRate, st.LastSendRate);
            var baseline = Math.Max(recvBase, sendBase);
            st.LastBaseline = baseline;

            if (st.RateAnomalyActive)
            {
                // 已告警：速率回落到 2 倍基线以下才解除
                if (maxRate <= baseline * 2.0)
                    st.RateAnomalyActive = false;
                else
                    alerts.Add("rate_anomaly");
            }
            else
            {
                if (baseline > 0 && maxRate > baseline * RateThreshold)
                {
                    st.RateAnomalyActive = true;
                    alerts.Add("rate_anomaly");
                }
            }
        }

        // R2 频繁断连：5 分钟内重连 > 3 次
        if (st.Reconnects.Count > 3)
            alerts.Add("flap");

        // R3 长期无数据：在线 > 30 分钟且最近 10 分钟无活动
        if (st.FirstOnlineAt != null
            && now - st.FirstOnlineAt.Value > NoDataOnlineMin
            && now - st.LastActivityAt > NoDataIdleMin)
            alerts.Add("no_data");

        return (alerts, st.LastRecvRate, st.LastSendRate);
    }

    private static void Push(Queue<double> q, double v, int max)
    {
        q.Enqueue(v);
        while (q.Count > max) q.Dequeue();
    }

    private static double Percentile(Queue<double> q, double p)
    {
        var arr = q.ToArray();
        Array.Sort(arr);
        var idx = (int)Math.Ceiling(p * arr.Length) - 1;
        if (idx < 0) idx = 0;
        return arr[idx];
    }

    /// <summary>排除 0 值后的分位数（0 = 无数据窗口，不应拉低基线）。全 0 返回 0。</summary>
    private static double PercentileNonZero(Queue<double> q, double p)
    {
        var arr = q.Where(v => v > 0.001).ToArray();
        if (arr.Length == 0) return 0;
        Array.Sort(arr);
        var idx = (int)Math.Ceiling(p * arr.Length) - 1;
        if (idx < 0) idx = 0;
        return arr[idx];
    }
}
