# EMQX 审计监控（v2 server）部署手册

## 概览

| 项 | 值 |
|---|---|
| 二进制 | emqx-monitor-server（Linux）/ emqx-monitor-server.exe（Windows） |
| 默认端口 | 9527（环境变量 `EMQX_MONITOR_PORT` 覆盖） |
| 数据目录 | Linux 默认 `~/.local/share/EmqxMonitor/`；Windows 默认 `%LOCALAPPDATA%\EmqxMonitor\`（环境变量 `EMQX_MONITOR_DB` 覆盖） |
| 采集精度 | 1 分钟（每 60 秒轮询一次 EMQX API） |
| 数据保留 | 30 天，自动清理 |
| 认证 | 首次启动引导页设置管理员账号；登录 Cookie 会话 24 小时；失败 5 次锁定 5 分钟 |

## ⚠️ 重要：时区

**数据按服务器本地时间存储和查询。** 请确保服务器时区设为业务时区（如 Asia/Shanghai），否则排行榜时间会偏移。

```bash
timedatectl set-timezone Asia/Shanghai
```

## Linux 部署（systemd）

```bash
# 1. 放置二进制
sudo mkdir -p /opt/emqx-monitor
sudo cp emqx-monitor-server /opt/emqx-monitor/

# 2. 安装 systemd 服务（模板见 deploy/emqx-monitor-server.service，按需改端口/db路径）
sudo cp deploy/emqx-monitor-server.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now emqx-monitor-server
systemctl status emqx-monitor-server

# 3. 访问 http://服务器IP:9527 → 首次设置管理员账号
# 4. 登录后在"配置"页填入 EMQX 地址 + API Key（Dashboard → 管理 → API 密钥）
```

**黑名单功能权限**：拉黑/解封调用 EMQX 黑名单 API（`/api/v5/banned`）+ 踢下线（`/clients/kickout/bulk`），API Key 需有相应权限。EMQX 6.x 若创建 Key 时限制了 scopes，需包含 `connections`（含 banned）与 `clients`（踢下线）范围；直接用 administrator 角色则无需配置。

**端口冲突**：改端口只需改 service 文件的 `EMQX_MONITOR_PORT`。

**HTTPS**（推荐，套反向代理）：
```nginx
# 反代示例（Nginx，或群晖反代）
server {
    listen 443 ssl;
    server_name monitor.example.com;
    ssl_certificate     /path/cert.pem;
    ssl_certificate_key /path/key.pem;
    location / {
        proxy_pass http://127.0.0.1:9527;
        proxy_set_header X-Forwarded-For $remote_addr;
    }
}
```

## Windows 部署

两种方式任选：

### 方式 A：直接运行（简单）

双击 `emqx-monitor-server.exe`，保持窗口常开。首次访问 `http://服务器IP:9527` 设置管理员。

### 方式 B：注册为系统服务（推荐，开机自启 + 崩溃自拉起）

用 NSSM：

```bat
nssm install EmqxMonitorServer "C:\emqx-monitor\emqx-monitor-server.exe"
nssm set EmqxMonitorServer AppDirectory C:\emqx-monitor
nssm set EmqxMonitorServer AppEnvironmentExtra EMQX_MONITOR_PORT=9527
nssm set EmqxMonitorServer Start SERVICE_AUTO_START
nssm start EmqxMonitorServer
```

Windows 防火墙放行 9527 端口。

## 使用流程（反查伪造数据包）

1. 收到投诉：某呼号在某时间段干扰/伪造数据
2. 打开监控 → 排行榜 → 选时间范围（精确到分钟）→ 选排序维度（字节量/消息数/包数）
3. 数据量最大的呼号即疑似伪造者（伪造者通常用自己真实呼号连接）
4. 点呼号展开查看 clientid 明细，确认是哪台设备
5. 导出 CSV 留档，封号

## 注意事项与已知限制

- **1 分钟精度的固有盲区**：客户端在线时间不足 1 分钟（闪连即断）可能完全不被记录。若投诉对象是"短暂闪现"，排行榜可能查不到，属于预期行为。
- **增量数据只在客户端在线时记录**：离线期间无流量即无记录（排行榜自然排后）。
- **重连标记**：客户端重连（计数器归零）会在明细中标记"重连"，频繁重连也是刷数据特征。
- **数据保留 30 天**：超期自动删除，无法恢复。需要更久请自行定期备份 db 文件（可直接复制，WAL 模式下建议先 `sqlite3 db 'PRAGMA wal_checkpoint;'` 或停止服务再复制）。
- **EMQX 版本**：适配 5.x/6.x（API Key 认证）。实测基准 5.8.6。
- **EMQX 健康 CPU 指标**：5.x nodes 接口无 CPU 百分比，面板显示的是系统 1 分钟负载（load1）。

## 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `EMQX_MONITOR_PORT` | 9527 | HTTP 监听端口（0.0.0.0） |
| `EMQX_MONITOR_DB` | 用户数据目录 | SQLite 文件完整路径 |

## 备份建议

数据在单个 SQLite 文件内，直接复制文件即备份。建议每天 cron 备份 + 保留 60 天：

```bash
# /etc/cron.d/emqx-monitor-backup
0 3 * * * root cp /opt/emqx-monitor/emqx-monitor-server.db /opt/emqx-monitor/backup/$(date +\%F).db
```
