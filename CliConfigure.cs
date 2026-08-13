namespace EmqxMonitor;

/// <summary>
/// CLI 一次性配置：--configure --emqx <url> --api-key <key> --api-secret <secret>
/// 保存连接参数 → 验证连接 → 自动设置主题统计 bridge（主题 FMO/RAW，webhook 自动取本机 IP）。
/// 连接失败不保存、不配 bridge；成功后服务以 systemd/计划任务运行即可采集。
/// </summary>
public static class CliConfigure
{
    public static async Task<(string? Error, string? Message)> RunAsync(string[] args)
    {
        string? GetArg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var emqx = GetArg("--emqx");
        var key = GetArg("--api-key");
        var secret = GetArg("--api-secret");
        if (string.IsNullOrEmpty(emqx) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
            return ("用法: fmo-audit-service --configure --emqx http://IP:PORT --api-key <key> --api-secret <secret>", null);

        // db 路径（与主程序一致：EMQX_MONITOR_DB 或用户数据目录）
        var dbPath = Environment.GetEnvironmentVariable("EMQX_MONITOR_DB");
        if (string.IsNullOrEmpty(dbPath))
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmqxMonitor");
            Directory.CreateDirectory(dataDir);
            dbPath = Path.Combine(dataDir, "emqx-monitor-server.db");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var port = int.TryParse(Environment.GetEnvironmentVariable("EMQX_MONITOR_PORT"), out var envPort) ? envPort : 9527;

        var db = new Database(dbPath);
        var settings = new AppSettings(db);
        var client = new EmqxClient();

        // 1) 验证连接（失败即返回，不保存）
        Console.WriteLine($"[1/3] 验证 EMQX 连接: {emqx}");
        var cerr = await client.ConfigureAsync(emqx, $"{key}:{secret}");
        if (cerr != null)
        {
            Console.WriteLine($"  失败: {cerr}");
            return ($"连接失败: {cerr}", null);
        }
        Console.WriteLine("  通过: /status 可达，API Key 有效");

        // 2) 保存配置
        settings.EmqxUrl = emqx.Trim().TrimEnd('/');
        settings.EmqxApiKey = key.Trim();
        settings.EmqxApiSecret = secret.Trim();
        Console.WriteLine($"[2/3] 已保存连接配置 → {dbPath}");

        // 3) 自动设置主题统计 bridge（主题默认 FMO/RAW，webhook 自动取本机 IP）
        var webhook = $"http://{WebHelpers.GetLanIp()}:{port}/api/ingest";
        var token = settings.IngestToken;
        const string topic = "FMO/RAW";
        Console.WriteLine($"[3/3] 设置主题统计 bridge: webhook={webhook} topic={topic}");
        var (berr, pending, failed) = await client.SetupTopicRuleAsync(webhook, token, topic);
        if (berr != null)
        {
            Console.WriteLine($"  失败: {berr}");
            return ($"连接成功但 bridge 配置失败: {berr}（连接参数已保存）", null);
        }
        if (!string.IsNullOrEmpty(failed))
        {
            Console.WriteLine($"  注意（尽力配置）: {failed}");
        }

        settings.TopicEnabled = true;
        settings.TopicName = topic;
        settings.TopicWebhookUrl = webhook;
        settings.TopicPending = pending;
        settings.TopicFailed = failed;

        // 4) EMQX 侧实际状态验证（权威）
        var status = await client.GetTopicRuleStatusAsync();
        if (status.Ok)
        {
            settings.TopicPending = null;
            settings.TopicFailed = null;
            Console.WriteLine("  通过: connector connected / bridge 存在 / 规则已启用");
            return (null, $"连接成功，bridge 已就绪（主题 {topic}，webhook {webhook}）");
        }

        Console.WriteLine($"  链路检查: connector={status.ConnectorStatus ?? "不存在"}({status.ConnectorReason}) " +
                          $"bridge={(status.MiddlewareExists ? "存在" : "无")} " +
                          $"rule={(status.RuleExists ? (status.RuleEnabled == true ? "已启用" : "未启用") : "无")}");
        var reason = !status.ConnectorExists ? "连接器不存在"
            : status.ConnectorStatus != "connected" ? $"连接器状态 {status.ConnectorStatus}（{status.ConnectorReason}）"
            : !status.MiddlewareExists ? "桥接不存在"
            : !status.RuleExists ? "规则不存在"
            : "规则未启用";
        return ($"bridge 链路不完整: {reason}（连接参数已保存，请检查 EMQX 侧后重新执行 --configure）", null);
    }
}
