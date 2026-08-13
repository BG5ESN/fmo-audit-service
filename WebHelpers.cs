using System.Globalization;

namespace EmqxMonitor;

/// <summary>Web 层共享辅助（时间范围解析 / CSV 转义），多端点文件共用</summary>
public static class WebHelpers
{
    /// <summary>时间范围解析：yyyy-MM-ddTHH:mm（服务器本地时间），跨度 ≤31 天</summary>
    public static (string From, string To, string? Error) ParseRange(string from, string to)
    {
        if (!DateTime.TryParseExact(from, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            || !DateTime.TryParseExact(to, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return ("", "", "时间格式应为 yyyy-MM-ddTHH:mm");
        if (t < f) return ("", "", "结束时间不能早于开始时间");
        if (t - f > TimeSpan.FromDays(31)) return ("", "", "时间跨度不能超过 31 天");
        return (f.ToString("yyyy-MM-dd HH:mm:00"), t.ToString("yyyy-MM-dd HH:mm:00"), null);
    }

    /// <summary>CSV 转义 + 公式注入防护（= + - @ 开头前缀 '，Excel 打开时不执行）</summary>
    public static string Csv(string v)
    {
        if (v.Length > 0 && (v[0] == '=' || v[0] == '+' || v[0] == '-' || v[0] == '@' || v[0] == '\t' || v[0] == '\r'))
            v = "'" + v;
        return v.Contains(',') || v.Contains('"') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }
}
