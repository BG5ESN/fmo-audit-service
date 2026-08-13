using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmqxMonitor;

public class NodeInfo
{
    public string? Node { get; set; }

    /// <summary>系统 1 分钟负载均值（EMQX 5.x 无 CPU 百分比字段）</summary>
    public double? Load1 { get; set; }

    public long? MemoryTotal { get; set; }
    public long? MemoryUsed { get; set; }
}

/// <summary>兼容性自检报告</summary>
public class CompatibilityReport
{
    public string Version { get; init; } = "";
    public bool Supported { get; init; }
    public List<CompatCheck> Checks { get; init; } = [];
    public string? SuggestedUpgrade { get; init; }
}

/// <summary>单项 API 检查结果</summary>
public class CompatCheck
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool Ok { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>EMQX banned 条目（username 粒度）</summary>
public class BannedEntry
{
    public string Who { get; init; } = "";
    public string? Reason { get; init; }
    public string? Until { get; init; } // RFC3339 或 "infinity"（永久）
    public string? By { get; init; }
}

/// <summary>主题统计链路状态（测试连接用）</summary>
public class TopicRuleStatus
{
    /// <summary>是否为 6.x（已废弃，恒 false；保留供前端 app.js 的 s.v6 判断兼容，当前仅支持 5.x bridge）</summary>
    public bool V6 { get; set; }

    public bool ConnectorExists { get; set; }
    public string? ConnectorStatus { get; set; } // connected / connecting / disconnected
    public string? ConnectorReason { get; set; }
    public bool MiddlewareExists { get; set; } // bridge 存在性
    public bool RuleExists { get; set; }
    public bool? RuleEnabled { get; set; }
    public bool Ok { get; set; }
}

public class ClientsResult
{
    public List<EmqxClientInfo> Clients { get; init; } = [];
    public string? Error { get; init; }
}

public class EmqxError
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public class ClientsResponse
{
    [JsonPropertyName("data")] public List<EmqxClientInfo> Data { get; set; } = [];
    [JsonPropertyName("meta")] public ClientsMeta? Meta { get; set; }
}

public class ClientsMeta
{
    [JsonPropertyName("count")] public long Count { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("hasnext")] public bool HasNext { get; set; }
}

/// <summary>EMQX 客户端信息</summary>
public class EmqxClientInfo
{
    [JsonPropertyName("clientid")] public string ClientId { get; set; } = "";
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("connected")] public bool Connected { get; set; }
    [JsonPropertyName("connected_at")] public string? ConnectedAt { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("ip_address")] public string? IpAddress { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("keepalive")] public int Keepalive { get; set; }
    [JsonPropertyName("recv_pkt")] public long RecvPkt { get; set; }
    [JsonPropertyName("send_pkt")] public long SendPkt { get; set; }
    [JsonPropertyName("recv_cnt")] public long RecvCnt { get; set; }
    [JsonPropertyName("send_cnt")] public long SendCnt { get; set; }
    [JsonPropertyName("recv_msg")] public long RecvMsg { get; set; }
    [JsonPropertyName("send_msg")] public long SendMsg { get; set; }
    [JsonPropertyName("recv_oct")] public long RecvOct { get; set; }
    [JsonPropertyName("send_oct")] public long SendOct { get; set; }
    [JsonPropertyName("recv_msg.qos0")] public long RecvMsgQos0 { get; set; }
    [JsonPropertyName("recv_msg.qos1")] public long RecvMsgQos1 { get; set; }
    [JsonPropertyName("recv_msg.qos2")] public long RecvMsgQos2 { get; set; }
    [JsonPropertyName("send_msg.qos0")] public long SendMsgQos0 { get; set; }
    [JsonPropertyName("send_msg.qos1")] public long SendMsgQos1 { get; set; }
    [JsonPropertyName("send_msg.qos2")] public long SendMsgQos2 { get; set; }
    [JsonPropertyName("recv_msg.dropped")] public long RecvMsgDropped { get; set; }
    [JsonPropertyName("send_msg.dropped")] public long SendMsgDropped { get; set; }
    [JsonPropertyName("inflight_cnt")] public long InflightCnt { get; set; }
    [JsonPropertyName("mqueue_len")] public long MqueueLen { get; set; }

    [JsonPropertyName("subscriptions_cnt")]
    public long SubscriptionsCnt { get; set; }

    [JsonPropertyName("node")] public string? Node { get; set; }
    [JsonPropertyName("proto_ver")] public int ProtoVer { get; set; }
    [JsonPropertyName("clean_start")] public bool CleanStart { get; set; }

    /// <summary>客户端属性（认证时服务端写入）：callsign=呼号, uid=用户编号。呼号追踪优先于此字段</summary>
    [JsonPropertyName("client_attrs")]
    [JsonConverter(typeof(TolerantStringDictConverter))]
    public Dictionary<string, string>? ClientAttrs { get; set; }

    /// <summary>呼号（client_attrs.callsign，可能为 null）</summary>
    public string? Callsign
        => ClientAttrs is { } attrs && attrs.TryGetValue("callsign", out var c) ? c : null;

    /// <summary>用户编号（client_attrs.uid，可能为 null）</summary>
    public string? Uid
        => ClientAttrs is { } attrs && attrs.TryGetValue("uid", out var u) ? u : null;
}

/// <summary>
/// client_attrs 容错字典转换器：EMQX 可能把 uid 存成 JSON 数字，而模型字段是 string，
/// 默认 Dictionary&lt;string,string&gt; 反序列化遇到数字会抛 JsonException，导致整个 /clients 采集崩溃。
/// 此转换器把非字符串值归一为字符串：number→原始文本、bool→"true"/"false"、null→空串。
/// </summary>
public sealed class TolerantStringDictConverter : JsonConverter<Dictionary<string, string>>
{
    public override Dictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("client_attrs 应为 JSON 对象");

        var dict = new Dictionary<string, string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("client_attrs 应为键值对对象");

            var key = reader.GetString()!;
            reader.Read();
            dict[key] = reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString()!,
                JsonTokenType.Number => GetRawText(ref reader),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => string.Empty,
                _ => GetRawText(ref reader)
            };
        }
        return dict;
    }

    /// <summary>取 reader 当前位置的原始 JSON 文本（数字等非字符串值）</summary>
    private static string GetRawText(ref Utf8JsonReader reader)
    {
        var bytes = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
        return Encoding.UTF8.GetString(bytes);
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var kv in value)
            writer.WriteString(kv.Key, kv.Value);
        writer.WriteEndObject();
    }
}

public class RuleInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("sql")] public string? Sql { get; init; }
    [JsonPropertyName("actions")] public List<string> Actions { get; init; } = [];
    [JsonPropertyName("from")] public List<string> From { get; init; } = [];
    [JsonPropertyName("enable")] public bool Enable { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; init; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }
    [JsonPropertyName("last_modified_at")] public string? LastModifiedAt { get; init; }
}

public class BridgeInfo
{
    [JsonPropertyName("type")] public string? Type { get; init; } // webhook / http
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("method")] public string? Method { get; init; } // post / put / get / delete
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("enable")] public bool Enable { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("direction")] public string? Direction { get; init; } // egress
    [JsonPropertyName("local_topic")] public string? LocalTopic { get; init; }
    [JsonPropertyName("connect_timeout")] public string? ConnectTimeout { get; init; }
    [JsonPropertyName("request_timeout")] public string? RequestTimeout { get; init; }
    [JsonPropertyName("retry_interval")] public string? RetryInterval { get; init; }
    [JsonPropertyName("max_retries")] public int? MaxRetries { get; init; }
    [JsonPropertyName("pool_size")] public int? PoolSize { get; init; }
    [JsonPropertyName("pool_type")] public string? PoolType { get; init; } // random / hash

    [JsonPropertyName("enable_pipelining")]
    public int? EnablePipelining { get; init; }

    [JsonPropertyName("ssl")] public JsonElement? Ssl { get; init; }
    [JsonPropertyName("resource_opts")] public JsonElement? ResourceOpts { get; init; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; init; }

    // ---- 以下仅 GET 返回时有值（运行时状态）----
    [JsonPropertyName("status")] public string? Status { get; init; } // connected / disconnected / connecting / inconsistent
    [JsonPropertyName("status_reason")] public string? StatusReason { get; init; }
    [JsonPropertyName("node_status")] public JsonElement? NodeStatus { get; init; }
}