# FMO Audit Service (FAS) 安装指南

> FMO 语音服务器的**审计与守护服务**：统计谁在说话、记录在线客户端、解包包头识别身份伪造并自动拉黑。
> 与 SAS 的关系：**SAS 认证身份入口，FAS 审计台站行为**——SAS 把关谁能进，FAS 盯着里面谁在干什么。

## 前置要求

| 项 | 要求 |
|---|---|
| MQTT Broker | EMQX 5.8+ 或 6.x（与 FMO 服务器同一套即可） |
| API 密钥 | EMQX Dashboard → 管理 → **API 密钥** → 新建（角色 administrator；secret 只显示一次） |
| 网络 | FAS 与 EMQX 互通；Webhook 地址需 EMQX 所有节点可达（异地集群用公网地址） |

## 一键安装（Linux）

```bash
curl -fsSL https://bg5esn.com/share/fmo/fas-installer/install.sh | sudo bash
```

安装内容：
- 二进制 → `/opt/fmo-fas/fmo-audit-service`（专用低权限用户 `fmo-audit` 运行）
- 数据库 → `/opt/fmo-fas/fmo-audit-service.db`
- 系统服务 → `fmo-fas.service`（开机自启，`Restart=on-failure`——OTA 更新自动重启）

### 首次配置

1. 浏览器访问 `http://<服务器IP>:9527`
2. 设置管理员账号（首次强制）
3. 配置页填入 EMQX 地址 + API 密钥（获取方法见页面提示）
4. 启用主题统计：Webhook 地址同局域网用 IP 快速选择按钮；异地集群改公网可达地址
5. 身份控制默认启用（最高保护）——包头身份与连接身份不一致的包，发送者会被自动拉黑

## 升级（三种方式）

| 方式 | 适用 | 操作 |
|---|---|---|
| **页面按钮** | 裸机/VM（systemd） | 配置页 → 版本与更新 → 检查更新 → 立即更新（自动下载校验，服务自动重启） |
| **命令行** | 同上/无 UI 场景 | `sudo /opt/fmo-fas/fmo-audit-service --update`（后执行 `systemctl restart fmo-fas`） |
| **Docker** | 容器部署 | `docker pull <镜像> && docker compose up -d`（容器内自动禁用自更新） |

更新安全性：下载走 HTTPS（传输完整性由 TLS 保障），来源为官方 bg5esn.com。

## 卸载

```bash
curl -fsSL https://bg5esn.com/share/fmo/fas-installer/uninstall.sh | sudo bash
```

## 常用命令

```bash
systemctl status fmo-fas        # 状态
journalctl -u fmo-fas -f        # 日志
systemctl restart fmo-fas       # 重启
fmo-audit-service --check       # 检查更新（二进制所在目录）
```

## 安全部署选项

- **内网直连**：默认，信任局域网
- **反代 HTTPS**（推荐公网）：Nginx/群晖反代 + 登录限流；Webhook 接口（/api/ingest）建议仅放行 EMQX 节点 IP
- **VPN**：Tailscale/WireGuard 组网，公网不暴露端口

## 数据与备份

- 数据文件：`/opt/fmo-fas/fmo-audit-service.db`（含审计事件/黑名单留痕/配置）
- 备份：`sqlite3 /opt/fmo-fas/fmo-audit-service.db ".backup /backup/fas-$(date +%F).db"`（cron 每日）
- 迁移：复制 db + 二进制即可；换机后 Cookie 会话失效属正常（重新登录）
