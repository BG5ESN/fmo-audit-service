using System.Security.Cryptography;

namespace EmqxMonitor;

/// <summary>
/// settings 表强类型访问层：消灭散落的魔法字符串键名。
/// 未设置（null）时返回安全默认值。
/// </summary>
public class AppSettings
{
    private readonly Database _db;

    public AppSettings(Database db) => _db = db;

    // ---- EMQX 连接 ----
    public string EmqxUrl { get => _db.GetSetting("emqx_url") ?? ""; set => _db.SetSetting("emqx_url", value); }
    public string EmqxApiKey { get => _db.GetSetting("emqx_api_key") ?? ""; set => _db.SetSetting("emqx_api_key", value); }
    public string EmqxApiSecret { get => _db.GetSetting("emqx_api_secret") ?? ""; set => _db.SetSetting("emqx_api_secret", value); }

    // ---- 身份控制 / 引导 / 反代 ----
    /// <summary>身份控制开关（默认启用 = 最高保护）</summary>
    public bool IdentityControlEnabled
    {
        get => _db.GetSetting("identity_control") != "0";
        set => _db.SetSetting("identity_control", value ? "1" : "0");
    }

    public bool TrustProxy
    {
        get => _db.GetSetting("trust_proxy") == "1";
        set => _db.SetSetting("trust_proxy", value ? "1" : "0");
    }

    public bool WizardDone
    {
        get => _db.GetSetting("wizard_done") == "1";
        set => _db.SetSetting("wizard_done", value ? "1" : "0");
    }

    /// <summary>webhook 内部 token（首次读取时生成并持久化）</summary>
    public string IngestToken
    {
        get
        {
            var t = _db.GetSetting("ingest_token");
            if (string.IsNullOrEmpty(t))
            {
                t = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                _db.SetSetting("ingest_token", t);
            }
            return t;
        }
    }

    // ---- 主题统计 ----
    public bool TopicEnabled
    {
        get => _db.GetSetting("topic_enabled") == "1";
        set => _db.SetSetting("topic_enabled", value ? "1" : "0");
    }

    public string TopicName { get => _db.GetSetting("topic_name") ?? "FMO/RAW"; set => _db.SetSetting("topic_name", value); }
    public string TopicWebhookUrl { get => _db.GetSetting("topic_webhook_url") ?? ""; set => _db.SetSetting("topic_webhook_url", value); }
    public string? TopicPending { get => _db.GetSetting("topic_pending"); set => _db.SetSetting("topic_pending", value ?? ""); }
    public string? TopicFailed { get => _db.GetSetting("topic_failed"); set => _db.SetSetting("topic_failed", value ?? ""); }
}
