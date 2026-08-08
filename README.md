# FMO Audit Service（Server 版）

基于 EMQX 的客户端审计监控：按呼号排行总流量、按主题（FMO/RAW）统计发包量、时间轴反查伪造数据包、实时在线列表、服务器健康监控、黑名单管控（拉黑/解封/留痕）、身份控制（包头身份一致性检测，伪造即自动拉黑）。

- **数据精度**：主题统计 10 秒 / 呼号统计 1 分钟，保留 30 天自动清理
- **支持 EMQX**：5.x（5.1+）与 6.x（自动适配，配置页可自检）
- **零依赖**：单文件自包含二进制（内置 .NET 运行时 + SQLite），无需安装任何环境

## 快速部署

### Linux（推荐）

```bash
tar xzf fmo-audit-service-v1.0.0-linux-x64.tar.gz
bash deploy-linux.sh          # 安装到 /opt/emqx-monitor + systemd 服务
```

部署完成后浏览器访问 `http://<服务器IP>:9527`：

1. **设置管理员账号**（首次强制）
2. **连接 EMQX**：地址（如 `http://192.168.1.100:18083`）+ API Key / Secret（EMQX Dashboard → 管理 → API 密钥）
3. **启用主题统计**：填主题（默认 `FMO/RAW`，自动在 EMQX 上配置规则引擎）

### Windows

解压 `fmo-audit-service-v1.0.0-win-x64.zip`，双击 `fmo-audit-service.exe` 运行（或注册为 NSSM 服务），访问 `http://<服务器IP>:9527`。

## 使用场景（反查伪造数据包）

1. 收到投诉：某呼号在某时间段干扰/伪造数据
2. 打开监控 → **主题统计** → 选时间范围 → 时间轴 hover 查看每 10 秒谁发的多
3. 点开呼号展开 clientid 明细，确认设备，导出 CSV 留档
4. **拉黑**：排行榜 / 主题统计 / **在线列表** → 呼号行「拉黑」→ 填原因 + 封禁时长（永久或临时，到期自动解除）→ 立即踢下线并禁止重连（调用 EMQX 黑名单 API）
5. **在线列表**：实时查看当前在线客户端（呼号/clientid/IP/连接时长/流量），每 60 秒采集一次，拉黑/解封后即时刷新
6. **身份控制**（默认启用，最高保护）：每个 FMO/RAW 数据包解包头（64 字节，含 UID/呼号），比对**包头声明身份**与**连接身份**（认证服务端写入）——不一致即身份伪造，**立即自动拉黑该连接身份**（踢下线+禁连+留痕 operator=身份控制）；管理员可关闭（降级为仅记录提醒）
7. **身份审计页**：KICK（身份不符）/ WARN（未知身份）/ FAIL（非法包）事件留证，含连接身份 vs 包头声明、处置结果，可过滤查询
8. **黑名单页**：查看当前生效黑名单（含到期时间）、一键解封、操作历史（谁在何时拉黑/解封了谁、原因）

## 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `EMQX_MONITOR_PORT` | 9527 | HTTP 监听端口 |
| `EMQX_MONITOR_DB` | 用户数据目录 | SQLite 数据库路径（如 `/opt/emqx-monitor/fmo-audit-service.db`） |

## 常见操作

- **版本自检**：配置页 → 兼容性自检（验证 EMQX 版本与 API 可用性）
- **清空统计**：配置页 → 数据管理 → 清空全部统计数据（保留配置）
- **完全重置**：配置页 → 重置审计监控工具（恢复首次安装状态，同时移除 EMQX 规则引擎）
- **数据备份**：直接复制 db 文件即可（建议每天 cron 备份）

## 开发者：打包发布

```bash
# Linux / macOS（或 Windows Git Bash）
bash script/build-release.sh 1.0.0 [tag]

# Windows PowerShell
powershell -ExecutionPolicy Bypass -File script\build-release.ps1 -Version 1.0.0 [-Tag]
```

产物在 `dist/`：Linux tar.gz + Windows zip + 源码包，各带 .sha256 校验。
要求：dotnet SDK、git；打包前工作区必须无未提交改动。

## 注意事项

- 数据按服务器本地时间存储，请确保服务器时区正确（`timedatectl set-timezone Asia/Shanghai`）
- 服务器时区/时钟错误会导致时间轴时间偏移
- 主题统计需要 EMQX 规则引擎能访问到本服务（默认放行内网网段，见部署文档）
