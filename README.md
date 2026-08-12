# FMO Audit Service (FAS)

FMO 语音服务器的**审计与守护服务**：统计谁在说话、反查伪造数据包、黑名单管控、逐包核对身份并自动处置。

- **排行榜**：按呼号聚合历史流量（字节/消息/包数），展开 clientid 明细，CSV 导出留档
- **主题统计**：FMO/RAW 发包时间轴（10 秒粒度，可切 1 分钟/5 分钟/1 小时），每桶 Top 呼号——配合投诉时间点反查干扰源
- **在线列表**：实时查看当前在线客户端（呼号/clientid/IP/连接时长/流量），60 秒刷新
- **身份控制**（默认启用）：逐包解析 FMO/RAW 包头（64 字节，含 UID/呼号），比对**包头声明身份**与**连接身份**——不一致即伪造，**立即自动拉黑**（踢下线 + 禁连 + 留痕）
- **黑名单**：拉黑/解封（永久或到期自动解除），操作留痕（谁、何时、原因）
- **身份审计**：KICK（身份不符）/ WARN（未知身份）/ FAIL（非法包）事件留证，可过滤查询
- **健康监控**：服务器资源（CPU/内存/磁盘/网络）+ EMQX 节点状态（连接数/消息速率/告警）
- **OTA 升级**：页面一键检查更新并自动替换重启

技术特性：单文件自包含二进制（内置 .NET 运行时 + SQLite），零依赖；主题统计 10 秒 / 呼号统计 1 分钟精度，保留 30 天自动清理；支持EMQX5.1+版本，不支持6.x商业版本

## 快速开始

### Linux（推荐）

```bash
curl -fsSL https://bg5esn.com/share/fmo/fas-installer/install.sh | sudo bash
```

安装到 `/opt/fmo-fas/`，注册 systemd 服务 `fmo-fas`（专用低权限用户运行，开机自启，OTA 更新自动重启）。

### Windows

推荐使用官方安装脚本（自动下载最新版，注册计划任务开机自启）：

```powershell
irm https://bg5esn.com/share/fmo/fas-installer/install.ps1 -OutFile "$env:TEMP\fas-install.ps1"; iex (Get-Content "$env:TEMP\fas-install.ps1" -Raw -Encoding UTF8)
```

安装到 `%LOCALAPPDATA%\FMOAuditService\`；或解压 zip 双击 `fmo-audit-service.exe` 直接运行。访问 `http://<服务器IP>:9527`。

### 首次配置

1. 访问 `http://<服务器IP>:9527`，设置管理员账号（首次强制）
2. 配置页填入 EMQX 地址（如 `http://192.168.1.100:18083`）+ API Key / Secret（EMQX Dashboard → 管理 → API 密钥）
3. 启用主题统计：填主题（默认 `FMO/RAW`），自动在 EMQX 上配置规则引擎
4. 身份控制默认启用（最高保护）——包头与连接身份不一致的发送者会被自动拉黑

## 反查伪造数据包

1. 收到投诉 → 主题统计 → 选时间范围 → 时间轴 hover 查看每 10 秒谁发得多
2. 展开呼号查看 clientid 明细，确认设备，导出 CSV 留档
3. 排行榜 / 主题统计 / 在线列表 → 呼号行「拉黑」→ 原因 + 时长（永久或临时）→ 立即踢下线并禁止重连

## 升级 / 卸载

| 操作 | 方式 |
|---|---|
| 升级 | 配置页「版本与更新」按钮；Linux 命令行 `sudo /opt/fmo-fas/fmo-audit-service --update`（systemd 自动重启；Windows 计划任务更新后需 `Start-ScheduledTask fmo-fas`） |
| 卸载 | Linux: `curl -fsSL https://bg5esn.com/share/fmo/fas-installer/uninstall.sh \| sudo bash`；Windows: `irm https://bg5esn.com/share/fmo/fas-installer/uninstall.ps1 -OutFile "$env:TEMP\fas-uninstall.ps1"; iex (Get-Content "$env:TEMP\fas-uninstall.ps1" -Raw -Encoding UTF8)` |

## 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `EMQX_MONITOR_PORT` | 9527 | HTTP 监听端口 |
| `EMQX_MONITOR_DB` | 用户数据目录 | SQLite 数据库路径（如 `/opt/fmo-fas/fmo-audit-service.db`） |
| `EMQX_MONITOR_TRUST_PROXY` | - | 反代场景信任 `X-Forwarded-For` 计算客户端 IP（设为 1 启用） |
| `FAS_UPDATE_URL` | `https://bg5esn.com/share/fmo/fas.json` | OTA 更新元数据地址（自托管可覆盖） |

## 数据与备份

- 统计与审计数据保留 30 天自动清理；配置、管理员、黑名单留痕不清理
- 备份：`sqlite3 fmo-audit-service.db ".backup /backup/fas-$(date +%F).db"`（WAL 模式下不要直接复制文件）

## 开发者：打包发布

```bash
bash script/build-all.sh 2.0.13 tag     # 多平台单文件编译（版本 = git tag，自动写入 csproj）
bash script/gen-meta.sh                  # 生成 OTA 元数据 fas.json（版本取最近 tag）
```

- 产物在 `dist/`：`fmo-audit-service-<rid>.tar.gz/.zip`（固定名，各带 .sha256），源码包 `fmo-audit-service-v<版本>-src.tar.gz`
- 上传：产物 → `https://bg5esn.com/share/fmo/fas/v<版本>/`，`fas.json` → `https://bg5esn.com/share/fmo/fas.json`
- Windows 对应 PowerShell 版：`script/build-all.ps1` / `script/gen-meta.ps1`
- 要求：dotnet SDK + git；版本必须是干净语义版本（x.y.z）；打包前工作区无未提交改动

## 注意事项

- 数据按服务器本地时间存储，请确保服务器时区正确（`timedatectl set-timezone Asia/Shanghai`）
- 客户端在线不足 1 分钟（闪连即断）可能不被排行榜记录，属预期行为
- 安装/运维细节（Windows 服务、反向代理 HTTPS、EMQX 集群）见 [安装指南](docs/fas-install-guide.md)

## License

MIT
