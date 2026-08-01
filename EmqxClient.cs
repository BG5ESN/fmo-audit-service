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
    private readonly HttpClient _http;

    private string _baseUrl = "";
    private string _apiKey = "";

    public EmqxClient()
    {
        // 显式禁用系统代理：面板连的是用户内网 EMQX，必须直连。
        // 默认 HttpClient 会读 HTTP_PROXY 环境变量/系统代理，
        // 且 .NET 的 NO_PROXY 不支持 CIDR 网段（如 192.168.1.0/24），
        // 内网请求被错误转发到代理，代理可能返回 400/拒绝。
        var handler = new SocketsHttpHandler
        {
            Proxy = null,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    /// <summary>是否已配置连接</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl) && !string.IsNullOrEmpty(_apiKey);

    /// <summary>直接设置凭据（启动时从持久化配置恢复，不探测连通性；失败会在采集时体现）</summary>
    public void SetCredentials(string baseUrl, string apiKey)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "http://" + url;
        _baseUrl = url;
        _apiKey = apiKey;
    }

    /// <summary>配置 EMQX 连接并验证连通性</summary>
    /// <returns>成功返回 null，失败返回错误消息</returns>
    public async Task<string?> ConfigureAsync(string baseUrl, string apiKey)
    {
        // 规范化：去掉尾部斜杠
        var url = baseUrl.Trim().TrimEnd('/');
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "http://" + url;

        // 第 1 步：无认证探测 /status，确认地址可达且是 EMQX
        var reachable = await ProbeStatusAsync(url);
        if (reachable != null)
            return reachable;

        // 第 2 步：带 key 验证 clients 接口
        var test = await GetClientsAsync(url, apiKey, limit: 1);
        if (test.Error != null)
        {
            return test.Error switch
            {
                "BAD_API_KEY_OR_SECRET" => "API Key 错误：请检查 key:secret 是否正确（Dashboard → 管理 → API 密钥）",
                "HTTP_401" => "认证失败（401）：请检查 API Key",
                "HTTP_404" => "地址不对：EMQX REST API 路径应为 /api/v5（检查 EMQX 版本是否为 5.x/6.x）",
                _ => $"连接失败：{test.Error}"
            };
        }

        _baseUrl = url;
        _apiKey = apiKey;
        return null;
    }

    /// <summary>无认证探测 /status，返回 null 表示可达；否则返回错误消息</summary>
    private async Task<string?> ProbeStatusAsync(string baseUrl)
    {
        try
        {
            using var resp = await _http.GetAsync($"{baseUrl}/status");
            if (resp.IsSuccessStatusCode)
                return null;  // 可达
            return $"地址可达但响应异常（HTTP {(int)resp.StatusCode}）：{baseUrl}/status";
        }
        catch (TaskCanceledException)
        {
            return "请求超时：EMQX 地址不可达或防火墙拦截";
        }
        catch (HttpRequestException ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            if (msg.Contains("refused", StringComparison.OrdinalIgnoreCase))
                return "连接被拒绝：EMQX 未启动或端口不对（默认 18083）";
            if (msg.Contains("name or service", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("nodename", StringComparison.OrdinalIgnoreCase))
                return "地址无法解析：请检查 EMQX 地址";
            return $"网络错误：{msg}";
        }
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
                // 带出原始响应体，方便诊断（截断 200 字符）
                var snippet = body.Length > 200 ? body[..200] : body;
                return new ClientsResult { Error = $"HTTP_{(int)resp.StatusCode}: {snippet}" };
            }

            var result = JsonSerializer.Deserialize<ClientsResponse>(body);
            return new ClientsResult { Clients = result?.Data ?? [] };
        }
        catch (TaskCanceledException)
        {
            return new ClientsResult { Error = "请求超时：EMQX 地址不可达或防火墙拦截" };
        }
        catch (HttpRequestException ex)
        {
            // 细化常见错误：连接拒绝 / DNS 失败
            var msg = ex.InnerException?.Message ?? ex.Message;
            if (msg.Contains("refused", StringComparison.OrdinalIgnoreCase))
                return new ClientsResult { Error = "连接被拒绝：EMQX 未启动或端口不对（默认 18083）" };
            if (msg.Contains("name or service", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("nodename", StringComparison.OrdinalIgnoreCase))
                return new ClientsResult { Error = "地址无法解析：请检查 EMQX 地址" };
            return new ClientsResult { Error = $"网络错误：{msg}" };
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

    /// <summary>获取节点列表（EMQX 进程负载/内存）。实测 5.8.6：返回裸数组（无 data 包装），
    /// memory_used/memory_total 是带单位字符串（如 "4.69G"），CPU 无百分比字段，用 load1 代替。</summary>
    public async Task<List<NodeInfo>> GetNodesAsync()
    {
        var list = new List<NodeInfo>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v5/nodes");
            req.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_apiKey)));
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return list;
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (!TryGetRootArray(doc.RootElement, out var nodes)) return list;
            foreach (var node in nodes)
            {
                list.Add(new NodeInfo
                {
                    Node = node.TryGetProperty("node", out var n) ? n.GetString() : null,
                    Load1 = node.TryGetProperty("load1", out var l1) && l1.TryGetDouble(out var ld) ? ld : null,
                    MemoryTotal = node.TryGetProperty("memory_total", out var mt) ? ParseMemSize(mt) : null,
                    MemoryUsed = node.TryGetProperty("memory_used", out var mu) ? ParseMemSize(mu) : null,
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>获取集群消息计数（messages.received / messages.sent）。实测 5.8.6：返回节点数组。</summary>
    public async Task<(long? Received, long? Sent)> GetMessageCountsAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v5/metrics");
            req.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_apiKey)));
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (null, null);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            // 数组根（多节点）取第一个；兼容 {data:...} 包装
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (!root.EnumerateArray().Any()) return (null, null);
                root = root[0];
            }
            else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0)
            {
                root = d[0];
            }
            long? recv = root.TryGetProperty("messages.received", out var mr) && mr.TryGetInt64(out var mrr) ? mrr : null;
            long? sent = root.TryGetProperty("messages.sent", out var ms) && ms.TryGetInt64(out var mss) ? mss : null;
            return (recv, sent);
        }
        catch { return (null, null); }
    }

    /// <summary>兼容两种响应外壳：裸数组 或 {data:[...]}</summary>
    private static bool TryGetRootArray(JsonElement root, out JsonElement.ArrayEnumerator arr)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            arr = root.EnumerateArray();
            return true;
        }
        if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
        {
            arr = d.EnumerateArray();
            return true;
        }
        arr = default;
        return false;
    }

    /// <summary>解析 EMQX 带单位内存字符串（"4.69G" / "512M" / "1234"）→ 字节数</summary>
    private static long? ParseMemSize(JsonElement el)
    {
        var s = el.GetString();
        if (string.IsNullOrEmpty(s)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(s.Trim(), @"^([\d.]+)\s*([KMGTP]?B?)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return null;
        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "" or "B" => (long)v,
            "K" or "KB" => (long)(v * 1024),
            "M" or "MB" => (long)(v * 1024 * 1024),
            "G" or "GB" => (long)(v * 1024 * 1024 * 1024),
            "T" or "TB" => (long)(v * 1024 * 1024 * 1024 * 1024),
            _ => null,
        };
    }

    /// <summary>获取活跃告警名列表（逗号分隔）</summary>
    public async Task<string> GetActiveAlarmsAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v5/alarms?activated=true");
            req.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_apiKey)));
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return "";
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return "";
            var names = new List<string>();
            foreach (var a in data.EnumerateArray())
            {
                if (a.TryGetProperty("activated", out var act) && act.ValueKind == JsonValueKind.True
                    && a.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n)
                    names.Add(n);
            }
            return string.Join(", ", names.Distinct());
        }
        catch { return ""; }
    }
}

public class NodeInfo
{
    public string? Node { get; set; }
    /// <summary>系统 1 分钟负载均值（EMQX 5.x 无 CPU 百分比字段）</summary>
    public double? Load1 { get; set; }
    public long? MemoryTotal { get; set; }
    public long? MemoryUsed { get; set; }
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
