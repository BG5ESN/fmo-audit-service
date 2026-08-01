namespace EmqxMonitor;

/// <summary>一次宿主健康采样</summary>
public class HostHealthSample
{
    public double? CpuPct { get; init; }
    public double? MemUsedPct { get; init; }
    public double? DiskUsedPct { get; init; }
    public double? NetRecvKbps { get; init; }
    public double? NetSendKbps { get; init; }
}

public interface IHostHealthCollector
{
    /// <summary>采集宿主健康；首次调用返回 null（差值采样需要两拍）</summary>
    HostHealthSample? Collect();
}

/// <summary>
/// 双平台宿主健康采集：
///  - Linux: /proc/stat (CPU)、/proc/meminfo (内存)、/proc/net/dev (网络)
///  - Windows: PerformanceCounter（CPU / 内存 / 网络）
///  - 磁盘: DriveInfo（跨平台）
/// 速率型指标（CPU/网络）都是差值/窗口采样，首次调用用于预热。
/// </summary>
public class HostHealthCollector : IHostHealthCollector
{
    private readonly object _lock = new();

    // Linux /proc 上次采样
    private long _prevCpuTotal = -1, _prevCpuIdle;
    private long _prevRx = -1, _prevTx;
    private DateTime _prevNetAt;

#if WINDOWS
    private System.Diagnostics.PerformanceCounter? _cpuCounter;
    private System.Diagnostics.PerformanceCounter? _memCounter;
    private readonly List<(System.Diagnostics.PerformanceCounter Rx, System.Diagnostics.PerformanceCounter Tx)> _net = new();
    private bool _countersReady;
#endif

    public HostHealthSample? Collect()
    {
        try
        {
            lock (_lock)
            {
#if WINDOWS
                return CollectWindows();
#else
                return CollectLinux();
#endif
            }
        }
        catch
        {
            return null;
        }
    }

#if !WINDOWS
    private HostHealthSample? CollectLinux()
    {
        var (cpu, mem) = ReadProcLinux();
        var net = ReadNetLinux();
        return new HostHealthSample
        {
            CpuPct = cpu,
            MemUsedPct = mem,
            DiskUsedPct = ReadDisk(),
            NetRecvKbps = net.Recv,
            NetSendKbps = net.Send,
        };
    }

    private (double? Cpu, double? Mem) ReadProcLinux()
    {
        double? cpu = null, mem = null;
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu "));
            if (line != null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                long total = 0, idle = 0;
                for (var i = 1; i < parts.Length; i++)
                {
                    if (!long.TryParse(parts[i], out var v)) continue;
                    total += v;
                    if (i == 4 || i == 5) idle += v;   // idle + iowait
                }
                if (_prevCpuTotal >= 0)
                {
                    var dt = total - _prevCpuTotal;
                    if (dt > 0)
                    {
                        var di = idle - _prevCpuIdle;
                        cpu = Math.Clamp(100.0 * (dt - di) / dt, 0, 100);
                    }
                }
                _prevCpuTotal = total;
                _prevCpuIdle = idle;
            }

            long memTotal = 0, memAvail = 0;
            foreach (var ml in File.ReadLines("/proc/meminfo"))
            {
                if (ml.StartsWith("MemTotal:")) memTotal = ParseKb(ml);
                else if (ml.StartsWith("MemAvailable:")) { memAvail = ParseKb(ml); break; }
            }
            if (memTotal > 0)
                mem = Math.Clamp(100.0 * (memTotal - memAvail) / memTotal, 0, 100);
        }
        catch { }
        return (cpu, mem);
    }

    private (double? Recv, double? Send) ReadNetLinux()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
            {
                var idx = line.IndexOf(':');
                if (idx < 0) continue;
                var iface = line[..idx].Trim();
                if (iface == "lo") continue;
                var parts = line[(idx + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 9) continue;
                if (long.TryParse(parts[0], out var r)) rx += r;
                if (long.TryParse(parts[8], out var t)) tx += t;
            }
        }
        catch { return (null, null); }

        var now = DateTime.UtcNow;
        if (_prevRx < 0)
        {
            _prevRx = rx; _prevTx = tx; _prevNetAt = now;
            return (null, null);
        }
        var secs = (now - _prevNetAt).TotalSeconds;
        var drx = rx - _prevRx;
        var dtx = tx - _prevTx;
        _prevRx = rx; _prevTx = tx; _prevNetAt = now;
        if (secs <= 0) return (null, null);
        return (Math.Max(0, drx / 1024.0 / secs), Math.Max(0, dtx / 1024.0 / secs));
    }

    private static long ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && long.TryParse(parts[1], out var v) ? v : 0;
    }
#endif

    /// <summary>磁盘使用率（跨平台：取第一个就绪的固定盘，Linux 为 /）</summary>
    private static double? ReadDisk()
    {
        var drive = DriveInfo.GetDrives()
            .FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed && d.TotalSize > 0);
        if (drive == null) return null;
        var used = drive.TotalSize - drive.AvailableFreeSpace;
        return Math.Clamp(100.0 * used / drive.TotalSize, 0, 100);
    }

#if WINDOWS
    private HostHealthSample? CollectWindows()
    {
        EnsureCounters();
        if (!_countersReady) return null;
        var cpu = _cpuCounter!.NextValue();
        double rx = 0, tx = 0;
        foreach (var (r, t) in _net)
        {
            rx += r.NextValue();
            tx += t.NextValue();
        }
        return new HostHealthSample
        {
            CpuPct = Math.Clamp(cpu, 0, 100),
            MemUsedPct = Math.Clamp(_memCounter!.NextValue(), 0, 100),
            DiskUsedPct = ReadDisk(),
            NetRecvKbps = rx / 1024.0,
            NetSendKbps = tx / 1024.0,
        };
    }

    private void EnsureCounters()
    {
        if (_countersReady) return;
        try
        {
            _cpuCounter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
            _memCounter = new System.Diagnostics.PerformanceCounter("Memory", "% Committed Bytes In Use");
            var cat = new System.Diagnostics.PerformanceCounterCategory("Network Interface");
            foreach (var inst in cat.GetInstanceNames())
            {
                if (inst.Contains("Loopback") || inst.Contains("lo", StringComparison.OrdinalIgnoreCase)) continue;
                _net.Add((
                    new System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Received/sec", inst),
                    new System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Sent/sec", inst)));
            }
            // 预热：速率型计数器首次 NextValue() 返回 0，先各读一次
            _cpuCounter.NextValue();
            _memCounter.NextValue();
            foreach (var (r, t) in _net) { r.NextValue(); t.NextValue(); }
            _countersReady = true;
        }
        catch
        {
            _countersReady = false;
        }
    }
#endif
}
