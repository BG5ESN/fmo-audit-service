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

    /// <summary>清空内存凭据（完全重置时调用，避免 configured 状态残留）</summary>
    public void ClearCredentials()
    {
        _baseUrl = "";
        _apiKey = "";
        _version = null;
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

    // ---------------- 主题统计：规则引擎自动配置（5.8.6 / 6.2.2 双版本） ----------------

    /// <summary>连接器/规则固定命名（单实例管理；重复调用幂等更新）</summary>
    public const string TopicConnectorName = "emqx-monitor-ingest";
    public const string TopicBridgeName = "emqx-monitor-bridge";
    public const string TopicActionName = "emqx-monitor-ingest-action";
    public const string TopicRuleName = "emqx-monitor-topic-rule";

    private string? _version;

    /// <summary>探测 EMQX 主版本：6.x 用 action 路径（connector→action→规则引用 action），5.x 用 bridge 路径</summary>
    private async Task<bool> IsV6Async()
    {
        var v = await GetEmqxVersionAsync();
        return v?.StartsWith("6.") == true;
    }

    /// <summary>获取 EMQX 版本（如 "6.2.2"），探测失败返回 null</summary>
    public async Task<string?> GetEmqxVersionAsync()
    {
        if (_version != null) return _version;
        var resp = await SendAsync(HttpMethod.Get, "/api/v5/nodes", null);
        if (resp.Error != null) return null;
        try
        {
            using var doc = JsonDocument.Parse(resp.Body!);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
            _version = root.TryGetProperty("version", out var v) ? v.GetString() : "";
        }
        catch { _version = ""; }
        return _version;
    }

    /// <summary>兼容性自检：版本 + 关键 API 探测</summary>
    public async Task<CompatibilityReport> CheckCompatibilityAsync()
    {
        var version = await GetEmqxVersionAsync() ?? "未知";
        var isV6 = version.StartsWith("6.");
        var checks = new List<CompatCheck>();
        checks.Add(await ProbeApiAsync("客户端列表", "/api/v5/clients?limit=1"));
        checks.Add(await ProbeApiAsync("节点/健康", "/api/v5/nodes"));
        checks.Add(await ProbeApiAsync("规则引擎-连接器", "/api/v5/connectors"));
        if (isV6)
            checks.Add(await ProbeApiAsync("规则引擎-动作(6.x 必需)", "/api/v5/actions"));
        else
            checks.Add(await ProbeApiAsync("规则引擎-桥接(5.x 必需)", "/api/v5/bridges"));
        var supported = version.StartsWith("5.") || version.StartsWith("6.");
        return new CompatibilityReport
        {
            Version = version,
            Supported = supported,
            Checks = checks,
            SuggestedUpgrade = supported ? null : "当前 EMQX 版本不在支持范围。请升级到 EMQX 5.1 或更高版本（本工具支持 5.x 与 6.x）",
        };
    }

    /// <summary>探测单个 API 是否存在（404=版本过低不支持；401=认证问题）</summary>
    private async Task<CompatCheck> ProbeApiAsync(string name, string path)
    {
        var resp = await SendAsync(HttpMethod.Get, path, null);
        if (resp.Error == null)
            return new CompatCheck { Name = name, Path = path, Ok = true, Note = "可用" };
        if (resp.Error.Contains("404"))
            return new CompatCheck { Name = name, Path = path, Ok = false, Note = "API 不存在——EMQX 版本过低" };
        return new CompatCheck { Name = name, Path = path, Ok = false, Note = $"访问失败: {resp.Error}（检查 API Key/网络）" };
    }

    /// <summary>创建/更新主题统计规则引擎，自动适配 EMQX 版本（5.x bridge / 6.x action），幂等</summary>
    public async Task<string?> SetupTopicRuleAsync(string webhookUrl, string token, string topic)
        => await IsV6Async()
            ? await SetupTopicRuleV6Async(webhookUrl, token, topic)
            : await SetupTopicRuleV5Async(webhookUrl, token, topic);

    /// <summary>删除主题统计规则引擎（适配版本），幂等</summary>
    public async Task<string?> RemoveTopicRuleAsync()
        => await IsV6Async() ? await RemoveTopicRuleV6Async() : await RemoveTopicRuleV5Async();

    // ---------- 6.x 路径（实测 6.2.2）：connector → action → 规则引用 "http:{action}" ----------
    // 规则动作不能直接引用 connector（actions.discarded 计数），必须创建 action 实体。
    // action parameters.body 用 "${.}"（整个输出 JSON），payload 由 monitor 端 base64 解码算字节。

    private async Task<string?> SetupTopicRuleV6Async(string webhookUrl, string token, string topic)
    {
        var uri = new Uri(webhookUrl);
        var baseUrl = uri.GetLeftPart(UriPartial.Authority);
        var path = uri.AbsolutePath;

        // 1) 连接器（基础地址 + token headers）
        var connectorBody = JsonSerializer.Serialize(new
        {
            type = "http",
            name = TopicConnectorName,
            description = "EMQX Monitor topic ingest",
            url = baseUrl,
            headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
                ["x-ingest-token"] = token
            },
            enable = true
        });
        // PUT 不允许 name/type 字段（6.x 实测 unknown_fields），只更新部分字段
        var connectorUpdateBody = JsonSerializer.Serialize(new
        {
            url = baseUrl,
            headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
                ["x-ingest-token"] = token
            },
            enable = true
        });
        var existing = await SendAsync(HttpMethod.Get, $"/api/v5/connectors/http:{TopicConnectorName}", null);
        if (existing.Error == null && existing.Body != null && existing.Body.Contains($"\"name\":\"{TopicConnectorName}\""))
        {
            var upd = await SendAsync(HttpMethod.Put, $"/api/v5/connectors/http:{TopicConnectorName}", connectorUpdateBody);
            if (upd.Error != null) return $"更新连接器失败: {upd.Error}";
        }
        else
        {
            var cre = await SendAsync(HttpMethod.Post, "/api/v5/connectors", connectorBody);
            if (cre.Error != null) return $"创建连接器失败: {cre.Error}";
        }

        // 2) action（egress：method/path/headers/body 模板）
        var actionBody = JsonSerializer.Serialize(new
        {
            type = "http",
            name = TopicActionName,
            connector = TopicConnectorName,
            enable = true,
            parameters = new
            {
                method = "post",
                path,
                headers = new Dictionary<string, string>
                {
                    ["content-type"] = "application/json",
                    ["x-ingest-token"] = token
                },
                body = "${.}"
            }
        });
        // PUT 不允许 name/type（6.x 实测 required 仅 connector+parameters）
        var actionUpdateBody = JsonSerializer.Serialize(new
        {
            connector = TopicConnectorName,
            enable = true,
            parameters = new
            {
                method = "post",
                path,
                headers = new Dictionary<string, string>
                {
                    ["content-type"] = "application/json",
                    ["x-ingest-token"] = token
                },
                body = "${.}"
            }
        });
        var existingAction = await SendAsync(HttpMethod.Get, $"/api/v5/actions/http:{TopicActionName}", null);
        if (existingAction.Error == null && existingAction.Body != null && existingAction.Body.Contains($"\"name\":\"{TopicActionName}\""))
        {
            var upd = await SendAsync(HttpMethod.Put, $"/api/v5/actions/http:{TopicActionName}", actionUpdateBody);
            if (upd.Error != null) return $"更新动作失败: {upd.Error}";
        }
        else
        {
            var cre = await SendAsync(HttpMethod.Post, "/api/v5/actions", actionBody);
            if (cre.Error != null) return $"创建动作失败: {cre.Error}";
        }

        // 3) 规则
        return await UpsertTopicRuleAsync(topic, $"http:{TopicActionName}");
    }

    private async Task<string?> RemoveTopicRuleV6Async()
    {
        var err = await DeleteTopicRuleAsync();
        if (err != null) return err;
        var adel = await SendAsync(HttpMethod.Delete, $"/api/v5/actions/http:{TopicActionName}", null);
        if (IsNotFound(adel.Error)) return $"删除动作失败: {adel.Error}";
        var cdel = await SendAsync(HttpMethod.Delete, $"/api/v5/connectors/http:{TopicConnectorName}", null);
        if (IsNotFound(cdel.Error)) return $"删除连接器失败: {cdel.Error}";
        return null;
    }

    // ---------- 5.x 路径（实测 5.8.6）：connector → bridge v2 → 规则引用 "webhook:{bridge}" ----------
    // 实测要点：规则 action 必须引用 bridge（引用 connector 校验通过但 actions 计数为 0/discarded）；
    // bridge body 必须模板化为完整 JSON；规则 SQL 不能用 length() 函数。

    private async Task<string?> SetupTopicRuleV5Async(string webhookUrl, string token, string topic)
    {
        var connectorBody = JsonSerializer.Serialize(new
        {
            type = "http",
            name = TopicConnectorName,
            description = "EMQX Monitor topic ingest",
            url = webhookUrl,
            headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
                ["x-ingest-token"] = token
            },
            enable = true,
            pool_size = 8,
            enable_pipelining = 100,
            connect_timeout = "15s"
        });

        // 1) 连接器：存在则更新，否则创建
        var existing = await SendAsync(HttpMethod.Get, $"/api/v5/connectors/http:{TopicConnectorName}", null);
        if (existing.Error == null && existing.Body != null && existing.Body.Contains($"\"name\":\"{TopicConnectorName}\""))
        {
            var upd = await SendAsync(HttpMethod.Put, $"/api/v5/connectors/http:{TopicConnectorName}", connectorBody);
            if (upd.Error != null) return $"更新连接器失败: {upd.Error}";
        }
        else
        {
            var cre = await SendAsync(HttpMethod.Post, "/api/v5/connectors", connectorBody);
            if (cre.Error != null) return $"创建连接器失败: {cre.Error}";
        }

        // 2) bridge v2：平铺 url/method/headers/body（实测 required: url）
        var bridgeBody = JsonSerializer.Serialize(new
        {
            type = "http",
            name = TopicBridgeName,
            description = "EMQX Monitor topic bridge",
            url = webhookUrl,
            method = "post",
            headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
                ["x-ingest-token"] = token
            },
            body = "{\"topic\":\"${topic}\",\"username\":\"${username}\",\"clientid\":\"${clientid}\",\"payload\":\"${payload}\",\"qos\":\"${qos}\"}",
            enable = true,
            max_retries = 2
        });
        var bridgeId = $"webhook:{TopicBridgeName}";
        var existingBridge = await SendAsync(HttpMethod.Get, $"/api/v5/bridges/{bridgeId}", null);
        if (existingBridge.Error == null && existingBridge.Body != null && existingBridge.Body.Contains($"\"name\":\"{TopicBridgeName}\""))
        {
            var upd = await SendAsync(HttpMethod.Put, $"/api/v5/bridges/{bridgeId}", bridgeBody);
            if (upd.Error != null) return $"更新桥接失败: {upd.Error}";
        }
        else
        {
            var cre = await SendAsync(HttpMethod.Post, "/api/v5/bridges", bridgeBody);
            if (cre.Error != null) return $"创建桥接失败: {cre.Error}";
        }

        // 3) 规则
        return await UpsertTopicRuleAsync(topic, $"webhook:{TopicBridgeName}");
    }

    private async Task<string?> RemoveTopicRuleV5Async()
    {
        var err = await DeleteTopicRuleAsync();
        if (err != null) return err;
        var bdel = await SendAsync(HttpMethod.Delete, $"/api/v5/bridges/webhook:{TopicBridgeName}", null);
        if (IsNotFound(bdel.Error)) return $"删除桥接失败: {bdel.Error}";
        var cdel = await SendAsync(HttpMethod.Delete, $"/api/v5/connectors/http:{TopicConnectorName}", null);
        if (IsNotFound(cdel.Error)) return $"删除连接器失败: {cdel.Error}";
        return null;
    }

    /// <summary>删除资源时 404/NOT_FOUND 视为"本来就不存在"，不算失败</summary>
    private static bool IsNotFound(string? err)
        => err != null && !err.Contains("404") && !err.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase);

    /// <summary>创建/更新规则（SQL: topic/# 通配，覆盖精确主题与子主题；client_attrs 用于 6.x 自动带出 callsign；
    /// base64_encode(payload)：二进制 payload 必须 base64 才能无损嵌入 webhook JSON——直接内嵌原始字节会破坏 JSON 导致消息被丢弃）</summary>
    private async Task<string?> UpsertTopicRuleAsync(string topic, string actionRef)
    {
        var ruleSql = $"SELECT clientid, username, topic, base64_encode(payload) as payload, qos, timestamp, client_attrs FROM \"{topic}/#\"";
        var ruleBody = JsonSerializer.Serialize(new
        {
            name = TopicRuleName,
            sql = ruleSql,
            actions = new[] { actionRef },
            enable = true,
            description = "EMQX Monitor topic ingest rule"
        });
        var ruleId = await FindRuleIdByNameAsync(TopicRuleName);
        if (ruleId != null)
        {
            var upd = await SendAsync(HttpMethod.Put, $"/api/v5/rules/{ruleId}", ruleBody);
            if (upd.Error != null) return $"更新规则失败: {upd.Error}";
        }
        else
        {
            var cre = await SendAsync(HttpMethod.Post, "/api/v5/rules", ruleBody);
            if (cre.Error != null) return $"创建规则失败: {cre.Error}";
        }
        return null;
    }

    /// <summary>删除主题统计规则</summary>
    private async Task<string?> DeleteTopicRuleAsync()
    {
        var ruleId = await FindRuleIdByNameAsync(TopicRuleName);
        if (ruleId != null)
        {
            var del = await SendAsync(HttpMethod.Delete, $"/api/v5/rules/{ruleId}", null);
            if (del.Error != null) return $"删除规则失败: {del.Error}";
        }
        return null;
    }

    /// <summary>按名称查找规则 ID</summary>
    private async Task<string?> FindRuleIdByNameAsync(string name)
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/v5/rules", null);
        if (resp.Error != null) return null;
        try
        {
            using var doc = JsonDocument.Parse(resp.Body!);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var r in data.EnumerateArray())
                {
                    if (r.TryGetProperty("name", out var n) && n.GetString() == name
                        && r.TryGetProperty("id", out var id))
                        return id.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    // ---------------- 黑名单（banned API，5.8.6 / 6.2.2 实测一致） ----------------
    // 语义：banned 只阻止新连接，不踢已连接客户端——拉黑必须两步：POST banned + kickout。
    // until 传 RFC3339（服务器自动转 UTC 存储）；"infinity" = 永久。

    /// <summary>拉黑一个呼号（username 粒度）并立即踢掉其在线连接。返回 (错误, 被踢客户端数)</summary>
    public async Task<(string? Error, int Kicked)> BanAsync(string who, string? reason, string? untilRfc3339)
    {
        // 1) 写入 EMQX banned（拒绝新连接）；ALREADY_EXISTS = 已在黑名单，幂等视为成功
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["as"] = "username",
            ["who"] = who,
            ["reason"] = reason,
            ["until"] = untilRfc3339 ?? "infinity",
        });
        var resp = await SendAsync(HttpMethod.Post, "/api/v5/banned", body);
        if (resp.Error != null && resp.Error != "ALREADY_EXISTS") return (resp.Error, 0);

        // 2) 查该呼号在线 clientid → 踢下线（banned 不自动踢已连接）
        var clients = await GetClientsByUsernameAsync(who);
        if (clients.Count == 0) return (null, 0);
        var kick = await SendAsync(HttpMethod.Post, "/api/v5/clients/kickout/bulk", JsonSerializer.Serialize(clients));
        return kick.Error == null ? (null, clients.Count) : ($"踢下线失败: {kick.Error}", 0);
    }

    /// <summary>查询某呼号（username 精确匹配）当前在线 clientid 列表</summary>
    public async Task<List<string>> GetClientsByUsernameAsync(string username)
    {
        var list = new List<string>();
        var resp = await SendAsync(HttpMethod.Get, $"/api/v5/clients?username={Uri.EscapeDataString(username)}&limit=10000", null);
        if (resp.Error != null) return list;
        try
        {
            using var doc = JsonDocument.Parse(resp.Body!);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var c in data.EnumerateArray())
                {
                    if (c.TryGetProperty("clientid", out var id) && id.GetString() is { Length: > 0 } cid)
                        list.Add(cid);
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>解封呼号（不在黑名单时幂等成功，不报错）</summary>
    public async Task<string?> UnbanAsync(string who)
    {
        var resp = await SendAsync(HttpMethod.Delete, $"/api/v5/banned/username/{Uri.EscapeDataString(who)}", null);
        if (resp.Error == null) return null;
        return resp.Error == "NOT_FOUND" ? null : resp.Error;   // 已不在黑名单 = 已解封
    }

    /// <summary>读取 EMQX 侧当前黑名单（username 粒度；管理页与本地流水对照用）</summary>
    public async Task<List<BannedEntry>> GetBannedAsync()
    {
        var list = new List<BannedEntry>();
        var resp = await SendAsync(HttpMethod.Get, "/api/v5/banned?limit=1000", null);
        if (resp.Error != null) return list;
        try
        {
            using var doc = JsonDocument.Parse(resp.Body!);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var b in data.EnumerateArray())
                {
                    var asType = b.TryGetProperty("as", out var a) ? a.GetString() : null;
                    var who = b.TryGetProperty("who", out var w) ? w.GetString() : null;
                    if (asType != "username" || string.IsNullOrEmpty(who)) continue;   // 只展示呼号粒度
                    list.Add(new BannedEntry
                    {
                        Who = who,
                        Reason = b.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
                        Until = b.TryGetProperty("until", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
                        By = b.TryGetProperty("by", out var by) && by.ValueKind == JsonValueKind.String ? by.GetString() : null,
                    });
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>通用带认证的请求（返回 Body + 错误码）</summary>
    private async Task<(string? Error, string? Body)> SendAsync(HttpMethod method, string path, string? jsonBody)
    {
        try
        {
            using var req = new HttpRequestMessage(method, $"{_baseUrl}{path}");
            req.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_apiKey)));
            if (jsonBody != null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                try
                {
                    var err = JsonSerializer.Deserialize<EmqxError>(body);
                    if (err?.Code != null) return (err.Code, body);
                }
                catch { }
                return ($"HTTP_{(int)resp.StatusCode}", body);
            }
            return (null, body);
        }
        catch (UriFormatException)
        {
            return ("未配置 EMQX 连接（URL 无效）", null);
        }
        catch (TaskCanceledException)
        {
            return ("请求超时", null);
        }
        catch (HttpRequestException ex)
        {
            return ($"网络错误: {ex.Message}", null);
        }
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
    public string? Until { get; init; }   // RFC3339 或 "infinity"（永久）
    public string? By { get; init; }
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
    /// <summary>客户端属性（认证时服务端写入）：callsign=呼号, uid=用户编号。呼号追踪优先于此字段</summary>
    [JsonPropertyName("client_attrs")]
    public Dictionary<string, string>? ClientAttrs { get; set; }

    /// <summary>呼号（client_attrs.callsign，可能为 null）</summary>
    public string? Callsign
        => ClientAttrs is { } attrs && attrs.TryGetValue("callsign", out var c) ? c : null;

    /// <summary>用户编号（client_attrs.uid，可能为 null）</summary>
    public string? Uid
        => ClientAttrs is { } attrs && attrs.TryGetValue("uid", out var u) ? u : null;
}
