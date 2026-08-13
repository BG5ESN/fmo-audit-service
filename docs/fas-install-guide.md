# FMO Audit Service (FAS) 安装指南

> FMO 语音服务器的**审计与守护服务**：统计谁在说话、记录在线客户端、解包包头识别身份伪造并自动拉黑。

## 前置要求

| 项 | 要求 |
|---|---|
| MQTT Broker | EMQX 5.8+ 与 FMO 服务器同一套即可 |
| API 密钥 | EMQX Dashboard → 管理 → **API 密钥** → 新建（角色 administrator；secret 只显示一次） |
| 网络 | FAS 与 EMQX 互通；Webhook 地址需 EMQX 所有节点可达（异地集群用公网地址） |

## 安装

### Linux（推荐）

```bash
curl -fsSL https://bg5esn.com/share/fmo/fas-installer/install.sh | sudo bash
```

安装内容：
- 二进制 → `/opt/fmo-fas/fmo-audit-service`（专用低权限用户 `fmo-audit` 运行）
- 数据库 → `/opt/fmo-fas/fmo-audit-service.db`
- 系统服务 → `fmo-fas.service`（开机自启，`Restart=on-failure`——OTA 更新自动重启；启用 systemd 沙箱）

### Windows

推荐使用官方安装脚本（自动下载最新版，注册计划任务开机自启）：

```powershell
irm https://bg5esn.com/share/fmo/fas-installer/install.ps1 -OutFile "$env:TEMP\fas-install.ps1"; iex (Get-Content "$env:TEMP\fas-install.ps1" -Raw -Encoding UTF8)
```

安装内容：
- 二进制 → `%LOCALAPPDATA%\FMOAuditService\fmo-audit-service.exe`
- 数据库 → `%LOCALAPPDATA%\FMOAuditService\fmo-audit-service.db`（由 `EMQX_MONITOR_DB` 显式指定）
- 系统任务 → 计划任务 `fmo-fas`（Windows 内置，零依赖；`AtStartup` 开机自启 + 崩溃自动重启）

计划任务管理（管理员 PowerShell）：

|项目| 说明/指令|
|---|---|
|日志| Get-Content -Wait "$env:LOCALAPPDATA\FMOAuditService\fas.log"|
|启动| Start-ScheduledTask -TaskName fmo-fas|
|停止| Stop-ScheduledTask -TaskName fmo-fas|
|重启| Stop-ScheduledTask fmo-fas; Start-ScheduledTask fmo-fas|
|升级| Stop-ScheduledTask fmo-fas; & "$env:LOCALAPPDATA\FMOAuditService\fmo-audit-service.exe" --update; Start-ScheduledTask fmo-fas|
|卸载| irm https://bg5esn.com/share/fmo/fas-installer/uninstall.ps1 -OutFile "$env:TEMP\fas-uninstall.ps1"; iex (Get-Content "$env:TEMP\fas-uninstall.ps1" -Raw -Encoding UTF8) |
|安装目录|%LOCALAPPDATA%\FMOAuditService\|

Windows 防火墙放行 9527 端口。

### 首次配置

1. 浏览器访问 `http://<服务器IP>:9527`
2. 设置管理员账号（首次强制）
3. 配置页填入 EMQX 地址 + API Key / Secret（获取方法见页面提示）
4. 启用主题统计：Webhook 地址同局域网用 IP 快速选择按钮；异地集群改公网可达地址
5. 身份控制默认启用（最高保护）——包头身份与连接身份不一致的包，发送者会被自动拉黑

## 升级

| 方式 | 操作 |
|---|---|
| 页面按钮 | 配置页 → 版本与更新 → 检查更新 → 立即更新（自动下载替换；Linux systemd / Windows 计划任务均自动重启） |
| 命令行 | Linux: `sudo /opt/fmo-fas/fmo-audit-service --update`（systemd 自动重启） |

更新安全性：下载走 HTTPS（传输完整性由 TLS 保障），来源为官方 bg5esn.com。

## 卸载

- **Linux**：

```bash
curl -fsSL https://bg5esn.com/share/fmo/fas-installer/uninstall.sh | sudo bash
```

- **Windows**：

```powershell
irm https://bg5esn.com/share/fmo/fas-installer/uninstall.ps1 -OutFile "$env:TEMP\fas-uninstall.ps1"; iex (Get-Content "$env:TEMP\fas-uninstall.ps1" -Raw -Encoding UTF8)
```

卸载会停止/删除服务并删除数据目录（含数据库），执行前请确认。

## 常用命令

```bash
systemctl status fmo-fas        # 状态
journalctl -u fmo-fas -f        # 日志
systemctl restart fmo-fas       # 重启
/opt/fmo-fas/fmo-audit-service --check   # 检查更新
```

## 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `EMQX_MONITOR_PORT` | 9527 | HTTP 监听端口 |
| `EMQX_MONITOR_DB` | 用户数据目录 | SQLite 数据库路径 |
| `EMQX_MONITOR_TRUST_PROXY` | - | 反代场景信任 `X-Forwarded-For` 计算客户端 IP（设为 1 启用） |
| `FAS_UPDATE_URL` | `https://bg5esn.com/share/fmo/fas.json` | OTA 更新元数据地址 |

## 安全部署选项

工具默认 HTTP，公网暴露方式由部署层自选：

- **A. 内网直连**：默认即可，信任局域网
- **B. 反代 HTTPS**（推荐公网）：Nginx/群晖反代 + 强制 HTTPS + 限流。示例：

```nginx
server {
    listen 443 ssl;
    server_name monitor.example.com;
    ssl_certificate     /path/cert.pem;
    ssl_certificate_key /path/key.pem;
    location / {
        proxy_pass http://127.0.0.1:9527;
        proxy_set_header X-Forwarded-For $remote_addr;   # 必须覆盖（trust_proxy 前提）
        proxy_set_header X-Forwarded-Proto $scheme;      # Secure Cookie 生效
        proxy_set_header Host $host;
    }
}
# 登录接口限流（防暴力破解，与工具内锁定叠加）：
limit_req_zone $binary_remote_addr zone=login:10m rate=10r/m;
location /api/login { limit_req zone=login burst=5; proxy_pass http://127.0.0.1:9527; }
```

  套反代时**必须**启用 trust_proxy（systemd 服务加 `Environment=EMQX_MONITOR_TRUST_PROXY=1` 或设置页配置），登录锁定才会按真实客户端 IP 计算；启用前确认反代覆盖 `X-Forwarded-For` 头（直连模式默认用 TCP 对端 IP，伪造该头无效）。
- **C. VPN 访问**（最稳）：Tailscale/WireGuard 组网，公网不暴露任何端口

## EMQX 集群部署（多服务器异地互联）

开箱即用（实测验证）：

- **API 集群视图**：EMQX 地址填集群**任意一个节点**即可，clients、banned 均为全集群视图
- **黑名单/身份控制跨节点**：banned 是集群级——任一节点拉黑，全集群拒绝该呼号
- **webhook 不重复**：每条消息只在入口节点触发一次规则，不会双计
- **注意**：Webhook 地址必须是 EMQX **所有节点都能访问**的地址（异地节点访问不到内网 IP 时 connector 创建会超时）；集群内节点 EMQX 版本应一致

## 数据与备份

- 数据文件：`/opt/fmo-fas/fmo-audit-service.db`（含统计/审计事件/黑名单留痕/配置/管理员哈希，请勿以 root 运行服务）
- 统计与审计数据保留 30 天自动清理；备份：`sqlite3 fmo-audit-service.db ".backup /backup/fas-$(date +%F).db"`（WAL 模式下不要直接复制文件）
- 迁移：复制 db + 二进制即可；换机后 Cookie 会话失效属正常（重新登录）

## 已知限制

- **1 分钟精度盲区**：客户端在线不足 1 分钟（闪连即断）可能完全不被记录——"短暂闪现"的干扰源在排行榜可能查不到，属预期行为
- **重连标记**：客户端重连（计数器归零）在明细中标记"重连"，频繁重连也是刷数据特征
- **EMQX 健康 CPU 指标**：5.x nodes 接口无 CPU 百分比，面板显示的是系统 1 分钟负载（load1）

## 参考

- EMQX REST API 文档（开发/集成时查 API 字段与端点）：https://docs.emqx.com/zh/emqx/latest/admin/api.html
