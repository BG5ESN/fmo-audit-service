using System.Net.Http.Headers;
using System.Buffers;
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
    private readonly HttpClient _http = new(new SocketsHttpHandler { Proxy = null, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromSeconds(15) };

    private string _baseUrl = "";
    private string _apiSecret = "";
    private string? _version;

    private const string TopicBridgeName = "fas-auth-bridge";
    private const string TopicRuleName = "fas-auth-rule";
    private static Dictionary<string, string> BridgeHeaders(string token) => new() { ["content-type"] = "application/json", ["x-ingest-token"] = token };

    /// <summary>配置 EMQX 连接并验证连通性</summary>
    /// <returns>成功返回 null，失败返回错误消息</returns>
    public async Task<string?> ConfigureAsync(string baseUrl, string apiSecret)
    {
        SetCredentials(baseUrl, apiSecret);

        // 第 1 步：无认证探测 /status，确认地址可达且是 EMQX
        var reachable = await ProbeStatusAsync();
        if (reachable != null)
        {
            ClearCredentials();
            return reachable;
        }

        // 第 2 步：带 key 验证 clients 接口
        var test = await GetClientsAsync(limit: 1);
        if (test.Error != null)
        {
            ClearCredentials();
            return test.Error switch
            {
                "BAD_API_KEY_OR_SECRET" => "API Key 错误：请检查 key:secret 是否正确（Dashboard → 管理 → API 密钥）",
                "HTTP_401" => "认证失败（401）：请检查 API Key",
                "HTTP_404" => "地址不对：EMQX REST API 路径应为 /api/v5（检查 EMQX 版本是否为 5.x）",
                _ => $"连接失败：{test.Error}"
            };
        }

        return null;
    }

    /// <summary>直接设置凭据（启动时从持久化配置恢复，不探测连通性；失败会在采集时体现）。
    /// 必须不探测——服务启动时 EMQX 可能还没起来，探测失败会导致凭据落不进内存，采集永远起不来。</summary>
    public void SetCredentials(string baseUrl, string apiSecret)
    {
        ClearCredentials();
        if (!baseUrl.StartsWith("http://") && !baseUrl.StartsWith("https://"))
            baseUrl = "http://" + baseUrl.Trim().TrimEnd('/');
        _baseUrl = baseUrl;
        _apiSecret = apiSecret;
    }

    /// <summary>清空内存凭据（完全重置时调用，避免 configured 状态残留）</summary>
    public void ClearCredentials()
    {
        _baseUrl = "";
        _apiSecret = "";
        _version = null;
    }

    /// <summary>是否已配置连接</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl) && !string.IsNullOrEmpty(_apiSecret);

    // ================================================================
    // HTTP 底层：统一认证请求 + 错误码解析
    // ================================================================
    /// <summary>显式凭据版底层请求：认证/错误码解析/超时/网络分类与已配置版完全一致，仅凭据来自参数。
    /// 用于 ConfigureAsync 验证连通性（凭据还没落内存，也不能落——验证失败不应污染状态）。</summary>
    private async Task<ApiResult> DoRequestAsync(HttpMethod method, string path, string? jsonBody = null, int timeoutSeconds = 60)
    {
        if (string.IsNullOrEmpty(_baseUrl) || string.IsNullOrEmpty(_apiSecret))
            return new ApiResult("未配置 EMQX 连接（URL 无效）", null);
        try
        {
            using var req = new HttpRequestMessage(method, $"{_baseUrl}{path}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_apiSecret)));
            if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var resp = await _http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                try
                {
                    var err = JsonSerializer.Deserialize<EmqxError>(body);
                    if (err?.Code != null) return new ApiResult(err.Code, body);
                }
                catch
                {
                    // ignored
                }

                return new ApiResult($"HTTP_{(int)resp.StatusCode}", body);
            }

            return new ApiResult(null, body);
        }
        catch (UriFormatException)
        {
            return new ApiResult("未配置 EMQX 连接（URL 无效）", null);
        }
        catch (TaskCanceledException)
        {
            return new ApiResult("请求超时", null);
        }
        catch (HttpRequestException ex)
        {
            return new ApiResult($"网络错误: {ex.Message}", null);
        }
    }

    /// <summary>带认证请求的结果。Ok = Error == null。Body 仅成功时非空。</summary>
    private readonly record struct ApiResult(string? Error, string? Body)
    {
        public bool Ok => Error == null;
    }

    /// <summary>判断 body 是否表示资源存在（响应体含 "\"name\":\"{name}\""）。
    /// GET 单资源成功且 body 含 name → 存在；其余（404/网络错/空 body）→ 不存在。</summary>
    private static bool ResourceExists(ApiResult r, string name) => r.Ok && !string.IsNullOrEmpty(r.Body) && r.Body!.Contains($"\"name\":\"{name}\"");


    // ================================================================
    // emqx api client
    // ================================================================ 

    /// <summary>获取服务状态</summary>
    private async Task<string?> ProbeStatusAsync()
    {
        var resp = await DoRequestAsync(HttpMethod.Get, "/status");
        return resp.Ok ? null : $"地址可达但响应异常（HTTP {resp.Error}）：{_baseUrl}/status";
    }

    /// <summary>获取 EMQX 版本</summary>
    public async Task<string?> GetEmqxVersionAsync()
    {
        if (_version != null) return _version;
        var resp = await DoRequestAsync(HttpMethod.Get, "/api/v5/nodes");
        if (!resp.Ok) return null;
        try
        {
            using var doc = JsonDocument.Parse(resp.Body!);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
            _version = GetStringProp(root, "version") ?? "";
        }
        catch
        {
            _version = "";
        }

        return _version;
    }

    /// <summary>拉取客户端列表</summary>
    public async Task<ClientsResult> GetClientsAsync(int limit = 1000)
    {
        var resp = await DoRequestAsync(HttpMethod.Get, $"/api/v5/clients?limit={limit}");
        if (resp.Ok)
        {
            var result = JsonSerializer.Deserialize<ClientsResponse>(resp.Body!);
            return new ClientsResult { Clients = result?.Data ?? [] };
        }

        var error = resp.Error;
        if (error != null && error.StartsWith("HTTP_", StringComparison.Ordinal) && resp.Body != null)
        {
            var snippet = resp.Body.Length > 200 ? resp.Body[..200] : resp.Body;
            error = $"{error}: {snippet}";
        }

        return new ClientsResult { Error = error };
    }

    /// <summary>获取单个客户端详情</summary>
    public async Task<EmqxClientInfo?> GetClientAsync(string clientId)
    {
        var resp = await DoRequestAsync(HttpMethod.Get, $"/api/v5/clients/{Uri.EscapeDataString(clientId)}");
        if (!resp.Ok) return null;
        try
        {
            return JsonSerializer.Deserialize<EmqxClientInfo>(resp.Body!);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取节点列表</summary>
    public async Task<List<NodeInfo>> GetNodesAsync()
    {
        var list = new List<NodeInfo>();
        var resp = await DoRequestAsync(HttpMethod.Get, "/api/v5/nodes");
        if (!resp.Ok) return list;
        try
        {
            using var doc = JsonDocument.Parse(resp.Body!);
            if (!TryGetRootArray(doc.RootElement, out var nodes)) return list;
            foreach (var node in nodes)
            {
                list.Add(new NodeInfo
                {
                    Node = GetStringProp(node, "node"),
                    Load1 = node.TryGetProperty("load1", out var l1) && l1.TryGetDouble(out var ld) ? ld : null,
                    MemoryTotal = node.TryGetProperty("memory_total", out var mt) ? ParseMemSize(mt) : null,
                    MemoryUsed = node.TryGetProperty("memory_used", out var mu) ? ParseMemSize(mu) : null,
                });
            }
        }
        catch
        {
        }

        return list;
    }

    /// <summary>获取集群消息计数</summary>
    public async Task<(long? Received, long? Sent)> GetMessageCountsAsync()
    {
        var resp = await DoRequestAsync(HttpMethod.Get, "/api/v5/metrics");
        if (!resp.Ok) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(resp.Body!);
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
        catch
        {
            return (null, null);
        }
    }

    /// <summary>获取活跃告警名列表（逗号分隔）</summary>
    public async Task<string> GetActiveAlarmsAsync()
    {
        var resp = await DoRequestAsync(HttpMethod.Get, "/api/v5/alarms?activated=true");
        if (!resp.Ok) return "";
        try
        {
            using var doc = JsonDocument.Parse(resp.Body!);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return "";
            var names = new List<string>();
            foreach (var a in data.EnumerateArray())
            {
                if (a.TryGetProperty("activated", out var act) && act.ValueKind == JsonValueKind.True && a.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n)
                    names.Add(n);
            }

            return string.Join(", ", names.Distinct());
        }
        catch
        {
            return "";
        }
    }

    /// <summary>获取 clientid 列表 -- 基于用户名</summary>
    public async Task<List<string>> GetClientsByUsernameAsync(string username)
    {
        var list = new List<string>();
        var resp = await DoRequestAsync(HttpMethod.Get, $"/api/v5/clients?username={Uri.EscapeDataString(username)}&limit=10000");
        if (!resp.Ok) return list;
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
        catch
        {
        }

        return list;
    }

    /// <summary>获取黑名单</summary>
    public async Task<List<BannedEntry>> GetBannedAsync()
    {
        var list = new List<BannedEntry>();
        var resp = await DoRequestAsync(HttpMethod.Get, "/api/v5/banned?limit=1000");
        if (!resp.Ok) return list;
        try
        {
            using var doc = JsonDocument.Parse(resp.Body!);
            if (doc.RootElement.TryGetProperty("data", out var data)) list.AddRange(from b in data.EnumerateArray() let asType = GetStringProp(b, "as") let who = GetStringProp(b, "who") where asType == "username" && !string.IsNullOrEmpty(who) select new BannedEntry { Who = who, Reason = GetStringProp(b, "reason"), Until = GetStringProp(b, "until"), By = GetStringProp(b, "by") });
        }
        catch
        {
        }

        return list;
    }

    /// <summary>移入黑名单并且断开链接 -- 基于用户名</summary>
    public async Task<(string? Error, int Kicked)> BanAsync(string who, string? reason, string? untilRfc3339)
    {
        // 1) 写入 EMQX banned（拒绝新连接）；ALREADY_EXISTS = 已在黑名单，幂等视为成功
        var body = JsonSerializer.Serialize(new Dictionary<string, object?> { ["as"] = "username", ["who"] = who, ["reason"] = reason, ["until"] = untilRfc3339 ?? "infinity" });
        var resp = await DoRequestAsync(HttpMethod.Post, "/api/v5/banned", body);
        if (!resp.Ok && resp.Error != "ALREADY_EXISTS") return (resp.Error, 0);

        // 2) 查该呼号在线 clientid → 踢下线（banned 不自动踢已连接）
        var clients = await GetClientsByUsernameAsync(who);
        if (clients.Count == 0) return (null, 0);
        var kick = await DoRequestAsync(HttpMethod.Post, "/api/v5/clients/kickout/bulk", JsonSerializer.Serialize(clients));
        return kick.Ok ? (null, clients.Count) : ($"踢下线失败: {kick.Error}", 0);
    }

    /// <summary>移出黑名单 -- 基于用户名</summary>
    public async Task<string?> UnbanAsync(string who)
    {
        var resp = await DoRequestAsync(HttpMethod.Delete, $"/api/v5/banned/username/{Uri.EscapeDataString(who)}");
        if (resp.Ok) return null;
        return resp.Error == "NOT_FOUND" ? null : resp.Error; // 已不在黑名单 = 已解封
    }

    /// <summary>获取ruleId -- 基于规则名</summary>
    private async Task<string?> FindRuleIdByNameAsync(string name)
    {
        var resp = await DoRequestAsync(HttpMethod.Get, "/api/v5/rules");
        if (!resp.Ok) return null;
        try
        {
            using var doc = JsonDocument.Parse(resp.Body!);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var r in data.EnumerateArray())
                {
                    if (r.TryGetProperty("name", out var n) && n.GetString() == name && r.TryGetProperty("id", out var id)) return id.GetString();
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    /// <summary>创建 Rule</summary>
    private async Task<RuleInfo?> CreateRuleAsync(string ruleName, string bridgeName, string topic)
    {
        var ruleBody = JsonSerializer.Serialize(new
        {
            name = ruleName,
            sql = $"SELECT clientid, username, topic, base64_encode(payload) as payload, qos, timestamp, client_attrs FROM \"{topic}/#\"",
            actions = new[] { "webhook:" + bridgeName },
            enable = true,
            description = "FAS topic rule"
        });
        var resp = await DoRequestAsync(HttpMethod.Post, "/api/v5/rules", ruleBody);
        if (!resp.Ok) return null;
        return JsonSerializer.Deserialize<RuleInfo>(resp.Body!);
    }

    /// <summary>更新 Rule</summary>
    private async Task<RuleInfo?> UpdateRuleAsync(string ruleName, string bridgeName, string topic)
    {
        var ruleId = await FindRuleIdByNameAsync(ruleName);
        if (ruleId == null) return null;
        var ruleBody = JsonSerializer.Serialize(new
        {
            name = ruleName,
            sql = $"SELECT clientid, username, topic, base64_encode(payload) as payload, qos, timestamp, client_attrs FROM \"{topic}/#\"",
            actions = new[] { "webhook:" + bridgeName },
            enable = true,
            description = "FAS topic rule"
        });
        var resp = await DoRequestAsync(HttpMethod.Put, $"/api/v5/rules/{ruleId}", ruleBody);
        return !resp.Ok ? null : JsonSerializer.Deserialize<RuleInfo>(resp.Body!);
    }

    /// <summary>删除 Rule -- 基于规则名</summary>
    private async Task<string?> DeleteRuleAsync(string ruleName)
    {
        var ruleId = await FindRuleIdByNameAsync(ruleName);
        if (ruleId == null) return null;
        var del = await DoRequestAsync(HttpMethod.Delete, $"/api/v5/rules/{ruleId}");
        return !del.Ok ? $"删除规则失败: {del.Error}" : null;
    }

    /// <summary>创建 Bridge</summary>
    private async Task<BridgeInfo?> CreateBridgeAsync(string bridgeName, string hookUrl, string token)
    {
        var bridgeBody = JsonSerializer.Serialize(new
        {
            type = "webhook",
            name = bridgeName,
            description = "FAS topic bridge",
            url = hookUrl,
            method = "post",
            headers = BridgeHeaders(token),
            body = "{\"topic\":\"${topic}\",\"username\":\"${username}\",\"clientid\":\"${clientid}\",\"payload\":\"${payload}\",\"qos\":\"${qos}\",\"client_attrs\":${client_attrs}}",
            enable = true,
            max_retries = 2
        });
        var resp = await DoRequestAsync(HttpMethod.Post, "/api/v5/bridges", bridgeBody);
        if (!resp.Ok) return null;
        return JsonSerializer.Deserialize<BridgeInfo>(resp.Body!);
    }

    /// <summary>更新 Bridge</summary>
    private async Task<BridgeInfo?> UpdateBridgeAsync(string bridgeName, string hookUrl, string token)
    {
        var bridgeBody = JsonSerializer.Serialize(new
        {
            type = "webhook",
            name = bridgeName,
            description = "FAS topic bridge",
            url = hookUrl,
            method = "post",
            headers = BridgeHeaders(token),
            body = "{\"topic\":\"${topic}\",\"username\":\"${username}\",\"clientid\":\"${clientid}\",\"payload\":\"${payload}\",\"qos\":\"${qos}\",\"client_attrs\":${client_attrs}}",
            enable = true,
            max_retries = 2
        });
        var resp = await DoRequestAsync(HttpMethod.Put, $"/api/v5/bridges/webhook:{bridgeName}", bridgeBody);
        if (!resp.Ok) return null;
        return JsonSerializer.Deserialize<BridgeInfo>(resp.Body!);
    }

    /// <summary>删除 Bridge</summary>
    private async Task<string?> DeleteBridgeAsync(string bridgeName)
    {
        var resp = await DoRequestAsync(HttpMethod.Delete, $"/api/v5/bridges/webhook:{bridgeName}");
        if (resp.Ok) return null;
        // 404/NOT_FOUND 视为"本来就不存在"，幂等成功
        if (resp.Error != null && (resp.Error.Contains("404") || resp.Error.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase))) return null;
        return $"删除桥接失败: {resp.Error}";
    }

    /// <summary>获取 Bridge</summary>
    private async Task<BridgeInfo?> GetBridgeAsync(string bridgeName)
    {
        var resp = await DoRequestAsync(HttpMethod.Get, $"/api/v5/bridges/webhook:{bridgeName}");
        if (!resp.Ok) return null;
        return JsonSerializer.Deserialize<BridgeInfo>(resp.Body!);
    }


    // ================================================================
    // 主题统计编排层
    // 复用上面的原子 CRUD（Create/Update/Delete Bridge + Create/Update/Delete Rule），
    // connector 无需显式建：POST /bridges 会自动建 type=http、name 同 bridge 的 backing connector，
    // 其 status 随 bridge 联动，GetTopicRuleStatusAsync 查这个自动 connector(http:{bridgeName}) 即可。
    // ================================================================

    /// <summary>创建/更新主题统计规则引擎，幂等。尽力配置：失败不中断——bridge/规则全部尝试，统一报告。
    /// 返回 (错误, 待确认步骤, 失败步骤)：集群环境下请求状态不可靠，最终以 GetTopicRuleStatusAsync 实际查询为准。
    /// 流程：Upsert bridge（connector 自动生成）→ Upsert rule（桥接失败则跳过）。
    /// 规则的 sql/action/description 已封装在 Create/UpdateRuleAsync 内部，编排层只传 (ruleName, bridgeName, topic)。</summary>
    public async Task<(string? Error, string? Pending, string? Failed)> SetupTopicRuleAsync(string webhookUrl, string token, string topic)
    {
        var pending = new List<string>();
        var failed = new List<string>();

        // 1) 桥接：存在则更新，否则创建。存在性以 GET 单资源成功为准（GetBridgeAsync != null）。
        var bridgeStep = await UpsertBridgeAsync(pending, TopicBridgeName, webhookUrl, token);
        if (!string.IsNullOrEmpty(bridgeStep)) failed.Add(bridgeStep);

        // 2) 规则；桥接失败则跳过（依赖未就绪）
        if (!string.IsNullOrEmpty(bridgeStep))
        {
            failed.Add("规则：跳过（依赖桥接未就绪）");
        }
        else
        {
            var ruleErr = await UpsertRuleAsync(pending, TopicRuleName, TopicBridgeName, topic);
            if (ruleErr != null) failed.Add(ruleErr);
        }

        return (null, pending.Count > 0 ? string.Join("、", pending) : null, failed.Count > 0 ? string.Join("；", failed) : null);
    }

    /// <summary>删除主题统计规则引擎，幂等。流程：删 rule → 删 bridge（自动 connector 随 bridge 一起被 EMQX 清理）。</summary>
    public async Task<string?> RemoveTopicRuleAsync()
    {
        var err = await DeleteRuleAsync(TopicRuleName);
        if (err != null) return err;
        return await DeleteBridgeAsync(TopicBridgeName);
    }

    /// <summary>Upsert Bridge：GET 查存在 → 存在 UpdateBridgeAsync，否则 CreateBridgeAsync。</summary>
    private async Task<string?> UpsertBridgeAsync(List<string> pending, string bridgeName, string webhookUrl, string token)
    {
        var existing = await GetBridgeAsync(bridgeName);
        if (existing != null) return await StepAsync(pending, "更新桥接", () => ToApiResult(UpdateBridgeAsync(bridgeName, webhookUrl, token)));
        return await StepAsync(pending, "创建桥接", () => ToApiResult(CreateBridgeAsync(bridgeName, webhookUrl, token)));
    }

    /// <summary>Upsert Rule：FindRuleIdByName 判存在 → 存在 UpdateRuleAsync，否则 CreateRuleAsync。</summary>
    private async Task<string?> UpsertRuleAsync(List<string> pending, string ruleName, string bridgeName, string topic)
    {
        var exists = await FindRuleIdByNameAsync(ruleName);
        if (exists != null) return await StepAsync(pending, "更新规则", () => ToApiResult(UpdateRuleAsync(ruleName, bridgeName, topic)));
        return await StepAsync(pending, "创建规则", () => ToApiResult(CreateRuleAsync(ruleName, bridgeName, topic)));
    }


    /// <summary>主题统计链路状态（「测试连接」按钮用）：connector / bridge / rule 四件套存在性与状态。</summary>
    public async Task<TopicRuleStatus> GetTopicRuleStatusAsync()
    {
        var status = new TopicRuleStatus { V6 = false };
        // 1) connector
        var conn = await DoRequestAsync(HttpMethod.Get, $"/api/v5/connectors/http:{TopicBridgeName}");
        status.ConnectorExists = ResourceExists(conn, TopicBridgeName);
        if (status.ConnectorExists && conn.Body != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(conn.Body);
                var root = doc.RootElement;
                status.ConnectorStatus = GetStringProp(root, "status");
                status.ConnectorReason = GetStringProp(root, "status_reason");
            }
            catch
            {
            }
        }

        // 2) bridge
        var bridge = await GetBridgeAsync(TopicBridgeName);
        status.MiddlewareExists = bridge != null;

        // 3) rule
        var ruleId = await FindRuleIdByNameAsync(TopicRuleName);
        status.RuleExists = ruleId != null;
        if (ruleId != null)
        {
            var rule = await DoRequestAsync(HttpMethod.Get, $"/api/v5/rules/{ruleId}");
            if (rule.Ok && rule.Body != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(rule.Body);
                    status.RuleEnabled = GetBoolProp(doc.RootElement, "enable");
                }
                catch
                {
                }
            }
        }

        // Ok 必须包含 connector 连接状态：exists 但 disconnected（EMQX 连不到 webhook）= 链路不通
        status.Ok = status is { ConnectorExists: true, ConnectorStatus: "connected", MiddlewareExists: true, RuleExists: true, RuleEnabled: true };
        return status;
    }


    /// <summary>把原子 CRUD 的 BridgeInfo?/RuleInfo? 转 StepAsync 需要的 ApiResult：
    /// null（失败）→ Error（StepAsync 据此走 failed 分支）；非 null（成功）→ Ok。
    /// ⚠️ 失败时丢失 DoRequestAsync 的具体错误码/超时分类——原子 CRUD 内部吞了 resp。
    /// 这意味着"超时"和"真失败"在此无法区分，统一进 failed。若需保留超时→pending 语义，
    /// 应让原子 CRUD 返回 (info, error) 元组而非裸 info。当前为简化首版，统一进 failed。</summary>
    private static async Task<ApiResult> ToApiResult<T>(Task<T?> op) where T : class
    {
        var info = await op;
        return info == null ? new ApiResult("创建/更新失败", null) : new ApiResult(null, null);
    }

    /// <summary>执行配置步骤：超时 → 记入 pending（不中断），真失败 → 返回错误消息（调用方记入 failed，不中断）</summary>
    private static async Task<string?> StepAsync(List<string> pending, string stepName, Func<Task<ApiResult>> op)
    {
        var r = await op();
        if (r.Ok) return null;
        if (r.Error!.Contains("超时"))
        {
            pending.Add(stepName);
            return null;
        }

        return $"{stepName}失败: {r.Error}";
    }

    /// <summary>兼容性自检：版本 + 关键 API 探测</summary>
    public async Task<CompatibilityReport> CheckCompatibilityAsync()
    {
        var version = await GetEmqxVersionAsync() ?? "未知";
        var checks = new List<CompatCheck>
        {
            await ProbeApiAsync("客户端列表", "/api/v5/clients?limit=1"),
            await ProbeApiAsync("节点/健康", "/api/v5/nodes"),
            await ProbeApiAsync("规则引擎-连接器", "/api/v5/connectors"),
            await ProbeApiAsync("规则引擎-桥接", "/api/v5/bridges")
        };
        var supported = version.StartsWith("5.");
        return new CompatibilityReport
        {
            Version = version,
            Supported = supported,
            Checks = checks,
            SuggestedUpgrade = supported ? null : "当前 EMQX 版本不在支持范围。请升级到 EMQX 5.x",
        };
    }

    /// <summary>探测单个 API 是否存在（404=版本过低不支持；401=认证问题）</summary>
    private async Task<CompatCheck> ProbeApiAsync(string name, string path)
    {
        var resp = await DoRequestAsync(HttpMethod.Get, path, null, 60);
        if (resp.Ok)
            return new CompatCheck { Name = name, Path = path, Ok = true, Note = "可用" };
        if (resp.Error!.Contains("404"))
            return new CompatCheck { Name = name, Path = path, Ok = false, Note = "API 不存在——EMQX 版本过低" };
        return new CompatCheck { Name = name, Path = path, Ok = false, Note = $"访问失败: {resp.Error}（检查 API Key/网络）" };
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

    private static string? GetStringProp(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBoolProp(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
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