using System.Text;
using System.Text.Json;

namespace EmqxMonitor;

/// <summary>主题统计端点：webhook ingest + 身份审计 + 规则引擎配置 + 查询/导出</summary>
public static class TopicEndpoints
{
    public static void MapTopicEndpoints(this WebApplication app, AppSettings settings, int port,
        TopicIngestService topicIngest, EmqxClient emqx, Database db, CollectorService collector)
    {
        // POST /api/ingest — EMQX 规则引擎消息事件入口（token 校验）
        // 注意：必须同步 await 读 body——异步 Task.Run 读 Request.Body 在响应返回后不可读
        app.MapPost("/api/ingest", async (HttpContext ctx, TopicIngestService ingest) =>
        {
            var token = settings.IngestToken;
            var got = ctx.Request.Headers["X-Ingest-Token"].FirstOrDefault();
            if (string.IsNullOrEmpty(token) || got == null
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(got), System.Text.Encoding.UTF8.GetBytes(token)))
                return Results.Json(new { ok = false, error = "invalid token" }, statusCode: 401);
            if (!ctx.Request.HasJsonContentType())
                return Results.Json(new { ok = false, error = "bad content type" }, statusCode: 400);

            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var root = doc.RootElement;
                var topic = root.TryGetProperty("topic", out var t) ? t.GetString() : null;
                var username = root.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                if (username == "undefined") username = null;   // EMQX 无用户名客户端的 Erlang undefined atom 序列化
                // 呼号优先 client_attrs.callsign（认证时服务端写入的属性，比 username 可靠）
                string? callsign = null, uid = null;
                if (root.TryGetProperty("client_attrs", out var ca) && ca.ValueKind == JsonValueKind.Object)
                {
                    if (ca.TryGetProperty("callsign", out var cs) && cs.ValueKind == JsonValueKind.String)
                        callsign = cs.GetString();
                    if (ca.TryGetProperty("uid", out var cu))
                    {
                        uid = cu.ValueKind == JsonValueKind.String ? cu.GetString() : cu.GetRawText();
                    }
                }
                if (!string.IsNullOrEmpty(callsign)) username = callsign;
                var clientid = root.TryGetProperty("clientid", out var c) ? c.GetString() : null;
                long bytes = 0;
                byte[]? raw = null;
                if (root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    var s = p.GetString()!;
                    try
                    {
                        raw = Convert.FromBase64String(s);
                        bytes = raw.Length;
                    }
                    catch { bytes = s.Length; }   // 非 base64 则按字符数近似
                }
                if (!string.IsNullOrEmpty(topic) && !string.IsNullOrEmpty(clientid))
                {
                    ingest.Ingest(topic, username, uid, clientid, bytes, DateTime.Now);
                    await RunIdentityAuditAsync(raw, topic, username, uid, clientid, DateTime.Now, topicIngest, db, emqx, collector);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Ingest] 解析失败: {ex.Message}");
            }
            return Results.Json(new { ok = true });
        });

        // GET /api/topic-config — 主题统计状态
        app.MapGet("/api/topic-config", () => Results.Json(new
        {
            ok = true,
            enabled = settings.TopicEnabled,
            topic = settings.TopicName,
            webhook_url = settings.TopicWebhookUrl,
            ingest_url = $"http://{GetLanIp()}:{port}/api/ingest",
            local_ips = GetAllLocalIps(),   // 本机所有 IP（Webhook 快速配置按钮）
            total_ingested = topicIngest.TotalIngested,
            last_ingest_at = topicIngest.LastIngestAt == default ? null : topicIngest.LastIngestAt.ToString("yyyy-MM-dd HH:mm:ss"),
            ingest_token = settings.IngestToken,
            pending = settings.TopicPending,   // 非空 = 有步骤超时待确认（集群场景）
            failed = settings.TopicFailed,     // 非空 = 有步骤失败（尽力配置报告）
        }));

        // POST /api/topic-config — 启用/停用主题统计（自动配置 EMQX 规则引擎）
        app.MapPost("/api/topic-config", async (TopicConfigRequest req) =>
        {
            var topic = string.IsNullOrWhiteSpace(req.Topic) ? "FMO/RAW" : req.Topic.Trim();
            if (req.Enable)
            {
                if (!emqx.IsConfigured)
                    return Results.Json(new { ok = false, error = "请先在 EMQX 连接配置中保存连接" });
                var webhookUrl = string.IsNullOrWhiteSpace(req.WebhookUrl) ? $"http://{GetLanIp()}:{port}/api/ingest" : req.WebhookUrl.Trim().TrimEnd('/');
                var token = settings.IngestToken;
                // 尽力配置：失败不中断，统一报告；集群下请求状态不可靠，报告以实际查询为准
                var (err, pending, failed) = await emqx.SetupTopicRuleAsync(webhookUrl, token, topic);
                // 立即启用：不阻塞——EMQX 规则引擎一旦生效即主动上报，配置报告只是信息
                settings.TopicEnabled = true;
                settings.TopicName = topic;
                settings.TopicWebhookUrl = webhookUrl;
                settings.TopicPending = pending;
                settings.TopicFailed = failed;
                // 实际状态（EMQX 侧真实查询，权威）
                var status = await emqx.GetTopicRuleStatusAsync();
                if (status.Ok) { settings.TopicPending = null; settings.TopicFailed = null; }
                return Results.Json(new
                {
                    ok = true,
                    enabled = true,   // 立即启用（配置报告不阻塞）
                    topic,
                    webhook_url = webhookUrl,
                    pending,
                    failed,
                    status = new
                    {
                        status.Ok,
                        connector = new { exists = status.ConnectorExists, state = status.ConnectorStatus, reason = status.ConnectorReason },
                        middleware = new { exists = status.MiddlewareExists, kind = status.V6 ? "action" : "bridge" },
                        rule = new { exists = status.RuleExists, enabled = status.RuleEnabled },
                    },
                    hint = status.Ok ? null
                        : "主题统计已启用（数据照常接收）。配置存在异常，请到 EMQX Dashboard → 集成 → 连接器/规则 查看，或修复后重新启用/点「测试连接」。",
                });
            }
            else
            {
                var err = await emqx.RemoveTopicRuleAsync();
                if (err != null)
                    return Results.Json(new { ok = false, error = err });
                settings.TopicEnabled = false;
                settings.TopicPending = null;
                settings.TopicFailed = null;
                return Results.Json(new { ok = true });
            }
        });

        // GET /api/topic-test — 测试主题统计链路（connector/bridge|action/rule 四件套真实状态）
        app.MapGet("/api/topic-test", async () =>
        {
            if (!emqx.IsConfigured)
                return Results.Json(new { ok = false, error = "未配置 EMQX 连接" });
            var status = await emqx.GetTopicRuleStatusAsync();
            // 测试通过则清除待确认标记
            if (status.Ok)
                settings.TopicPending = null;
            return Results.Json(new
            {
                ok = true,
                status = new
                {
                    status.Ok,
                    status.V6,
                    connector = new { exists = status.ConnectorExists, state = status.ConnectorStatus, reason = status.ConnectorReason },
                    middleware = new { exists = status.MiddlewareExists, kind = status.V6 ? "action" : "bridge" },
                    rule = new { exists = status.RuleExists, enabled = status.RuleEnabled },
                },
                dashboard_hint = status.Ok
                    ? null
                    : "链路不完整。请到 EMQX Dashboard → 集成 → 连接器/规则 查看真实状态，或重新启用主题统计",
            });
        });

        // GET /api/topic-leaderboard — 主题发包量排行（按呼号）
        app.MapGet("/api/topic-leaderboard", (string from, string to, string? order, int? limit, Database database) =>
        {
            var (f, t, err) = WebHelpers.ParseRange(from, to);
            if (err != null) return Results.Json(new { ok = false, error = err });
            var topic = settings.TopicName;
            var rows = database.QueryTopicLeaderboard(topic, f, t, order ?? "msg", Math.Clamp(limit ?? 100, 1, 1000));
            return Results.Json(new { ok = true, topic, from = f, to = t, order = order ?? "msg", rows });
        });

        // GET /api/topic-leaderboard/{name} — 呼号在主题上的 clientid 明细
        app.MapGet("/api/topic-leaderboard/{name}", (string name, string from, string to, Database database) =>
        {
            var (f, t, err) = WebHelpers.ParseRange(from, to);
            if (err != null) return Results.Json(new { ok = false, error = err });
            var topic = settings.TopicName;
            var rows = database.QueryTopicDetail(topic, name, f, t);
            return Results.Json(new { ok = true, name, topic, from = f, to = t, rows });
        });

        // GET /api/topic-timeline — 时间轴（全员总量按时间桶聚合，1m/5m/1h）
        app.MapGet("/api/topic-timeline", (string from, string to, string? bucket, Database database) =>
        {
            var (f, t, err) = WebHelpers.ParseRange(from, to);
            if (err != null) return Results.Json(new { ok = false, error = err });
            var topic = settings.TopicName;
            var b = bucket is "10s" or "5m" or "1h" ? bucket : "1m";
            var rows = database.QueryTopicTimeline(topic, f, t, b);
            if (rows.Count > 40000)
                return Results.Json(new { ok = false, error = "时间范围过大（补零后超过 4 万点）。请缩小时间范围或使用更粗的统计周期（如 1 小时）" });
            return Results.Json(new { ok = true, topic, from = f, to = t, bucket = b, rows });
        });

        // GET /api/topic-export.csv — 主题排行 CSV 导出
        app.MapGet("/api/topic-export.csv", (string from, string to, string? order, Database database) =>
        {
            var (f, t, err) = WebHelpers.ParseRange(from, to);
            if (err != null) return Results.Json(new { ok = false, error = err });
            var topic = settings.TopicName;
            var rows = database.QueryTopicLeaderboard(topic, f, t, order ?? "msg", 5000);
            var sb = new StringBuilder();
            sb.Append($"\uFEFF排名,呼号,设备数,消息数,字节数,主题\n");
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                sb.Append($"{i + 1},{WebHelpers.Csv(r.Name)},{r.DeviceCount},{r.TotalMsg},{r.TotalBytes},{WebHelpers.Csv(topic)}\n");
            }
            return Results.Text(sb.ToString(), "text/csv; charset=utf-8");
        });

    }

    // 包头审计（身份控制）：解包 FMO/RAW 包头 → 比对连接身份 → KICK 自动拉黑 / WARN 仅记录 / FAIL 降级
    private static async Task RunIdentityAuditAsync(byte[]? raw, string? topic, string? connCallsign, string? connUid, string? clientid, DateTime now,
    TopicIngestService topicIngest, Database db, EmqxClient emqx, CollectorService collector)
    {
        if (raw == null || raw.Length == 0) return;   // 无 payload（文本统计模式）不审计
        var parsed = FmoRawParser.Parse(raw);
        var ts = now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        if (!parsed.Ok)
        {
            // FAIL：非法包（长度/len 不符/超 MTU），降级仅记录，不处置；限流防刷库放大
            if (!topicIngest.FailThrottled())
            {
                try { db.WriteAuditPacket(new AuditPacketRow { Ts = ts, Topic = topic ?? "", ClientId = clientid ?? "", Verdict = "FAIL", Len = raw.Length }); }
                catch (Exception ex) { Console.Error.WriteLine($"[Audit] FAIL 落库失败: {ex.Message}"); }
            }
            return;
        }

        var pktCallsign = parsed.Callsign.Trim().ToUpperInvariant();
        var pktUid = parsed.Uid.ToString();
        var connCs = (connCallsign ?? "").Trim().ToUpperInvariant();
        var connU = connUid ?? "";

        string verdict;
        if (string.IsNullOrEmpty(connCs) && string.IsNullOrEmpty(connU))
        {
            verdict = "WARN";   // 连接无身份（匿名）→ 无法比对，仅记录
        }
        else
        {
            var csOk = !string.IsNullOrEmpty(pktCallsign) && pktCallsign == connCs;
            var uidOk = !string.IsNullOrEmpty(pktUid) && pktUid == connU;
            verdict = csOk && uidOk ? "PASS" : "KICK";
        }

        if (verdict == "PASS") return;   // 放行（topic_stats 已聚合）

        // 异常事件：KICK（可自动拉黑）/ WARN
        var ban = false;
        if (verdict == "KICK" && topicIngest.IdentityControlEnabled && !string.IsNullOrEmpty(connCs))
        {
            var reason = $"身份控制: 包头声明 {pktCallsign}(UID {pktUid}) 与连接身份 {connCs}{(connU.Length > 0 ? $"(UID {connU})" : "")} 不符";
            try
            {
                var (err, _) = await emqx.BanAsync(connCs, reason, null);
                if (err == null)
                {
                    ban = true;
                    try
                    {
                        db.AddBlacklistEvent("ban", "username", connCs, reason, null, "身份控制", now);
                        _ = collector.CollectNowAsync();   // 即时刷新在线列表
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[Audit] 拉黑留痕失败: {ex.Message}"); }
                }
                else
                {
                    Console.Error.WriteLine($"[Audit] 自动拉黑 {connCs} 失败: {err}");
                }
            }
            catch (Exception ex)
            {
                // 防御：EMQX 未配置/URL 异常等不阻断审计落库
                Console.Error.WriteLine($"[Audit] 自动拉黑 {connCs} 异常: {ex.Message}");
            }
        }

        try
        {
            db.WriteAuditPacket(new AuditPacketRow
            {
                Ts = ts,
                Topic = topic ?? "",
                ClientId = clientid ?? "",
                ConnCallsign = connCallsign,
                ConnUid = connUid,
                PktCallsign = pktCallsign,
                PktUid = pktUid,
                Verdict = verdict,
                Len = parsed.Len,
                FrameNum = parsed.FrameNum,
                CrcOk = parsed.CrcOk,
                Smeter = parsed.Smeter,
                SrvUid = parsed.SrvUid.ToString(),
                PktTs = parsed.Timestamp.ToString(),
                StreamBegin = parsed.StreamBeginUtc.ToString(),
                Ban = ban,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Audit] 事件落库失败: {ex.Message}");
        }
    }

static string GetLanIp()
    {
        try
        {
            using var sock = new System.Net.Sockets.UdpClient("8.8.8.8", 80);   // 不发包，仅触发路由选择
            var ip = (sock.Client.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();
            return string.IsNullOrEmpty(ip) || ip == "0.0.0.0" ? "127.0.0.1" : ip;
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    // 本机所有非回环 IPv4（Webhook 快速配置用：Linux 多网卡常见多 IP，如 eth0/eth1/docker0）
    static List<string> GetAllLocalIps()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;   // 回环 / 链路本地
                    if (!list.Contains(ip)) list.Add(ip);
                }
            }
        }
        catch { }
        return list;
    }
}
