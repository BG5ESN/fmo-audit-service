using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmqxMonitor;

/// <summary>
/// EMQX v5 REST API 客户端。
/// 认证：API Key（key:secret）走 HTTP Basic Auth（5.8 实测，Dashboard 账号无效）。
/// </summary>
public class EmqxClient
{
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private string _baseUrl = "";
    private string _apiKey = "";

    /// <summary>是否已配置连接</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl) && !string.IsNullOrEmpty(_apiKey);

    /// <summary>配置 EMQX 连接并验证连通性</summary>
    /// <returns>成功返回 null，失败返回错误消息</returns>
    public async Task<string?> ConfigureAsync(string baseUrl, string apiKey)
    {
        // 规范化：去掉尾部斜杠
        var url = baseUrl.Trim().TrimEnd('/');
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "http://" + url;

        // 先验证连通性：拉 1 条客户端
        var test = await GetClientsAsync(url, apiKey, limit: 1);
        if (test.Error != null)
        {
            return test.Error switch
            {
                "BAD_API_KEY_OR_SECRET" => "API Key 错误：请检查 key:secret 是否正确（Dashboard → 管理 → API 密钥）",
                "HTTP_401" => "认证失败（401）：请检查 API Key",
                "HTTP_404" => "地址不对：EMQX REST API 路径应为 /api/v5（检查 EMQX 版本是否为 5.x）",
                _ => $"连接失败：{test.Error}"
            };
        }

        _baseUrl = url;
        _apiKey = apiKey;
        return null;
    }

    /// <summary>拉取客户端列表（最多 limit 条，实测 limit=10000 可用）</summary>
    public async Task<ClientsResult> GetClientsAsync(int limit = 10000)
        => await GetClientsAsync(_baseUrl, _apiKey, limit);

    private async Task<ClientsResult> GetClientsAsync(string baseUrl, string apiKey, int limit)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v5/clients?limit={limit}");
            req.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(apiKey)));

            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                // 尝试解析 EMQX 错误码
                try
                {
                    var err = JsonSerializer.Deserialize<EmqxError>(body);
                    if (err?.Code != null)
                        return new ClientsResult { Error = err.Code };
                }
                catch { }
                return new ClientsResult { Error = $"HTTP_{(int)resp.StatusCode}" };
            }

            var result = JsonSerializer.Deserialize<ClientsResponse>(body);
            return new ClientsResult { Clients = result?.Data ?? [] };
        }
        catch (TaskCanceledException)
        {
            return new ClientsResult { Error = "请求超时：EMQX 地址不可达" };
        }
        catch (HttpRequestException ex)
        {
            return new ClientsResult { Error = $"网络错误：{ex.Message}" };
        }
    }

    /// <summary>获取单个客户端详情（不存在返回 null）</summary>
    public async Task<EmqxClientInfo?> GetClientAsync(string clientId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v5/clients/{Uri.EscapeDataString(clientId)}");
            req.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_apiKey)));
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<EmqxClientInfo>(body);
        }
        catch
        {
            return null;
        }
    }
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

/// <summary>EMQX 客户端信息（字段对应 5.8.6 实测返回）</summary>
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
    [JsonPropertyName("subscriptions_cnt")] public long SubscriptionsCnt { get; set; }
    [JsonPropertyName("node")] public string? Node { get; set; }
    [JsonPropertyName("proto_ver")] public int ProtoVer { get; set; }
    [JsonPropertyName("clean_start")] public bool CleanStart { get; set; }
}
