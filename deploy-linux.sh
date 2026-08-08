#!/bin/bash
# EMQX 审计监控工具 — Linux 一键部署脚本
# 用法: 解压 tar.gz 后 bash deploy-linux.sh [安装目录]
# 默认安装到 /opt/emqx-monitor（systemd 服务 emqx-monitor）
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
D="${1:-/opt/emqx-monitor}"

echo "== EMQX 审计监控工具部署 =="
echo "安装目录: $D"

# 1. 创建安装目录 + 专用低权限用户（db 含 EMQX Secret，禁止 root 运行）
sudo mkdir -p "$D"
if ! id emqx-monitor >/dev/null 2>&1; then
  sudo useradd --system --no-create-home --home-dir "$D" --shell /usr/sbin/nologin emqx-monitor
fi
sudo cp "$DIR/fmo-audit-service" "$D/"
[ -f "$DIR/fmo-audit-service.service" ] && sudo cp "$DIR/fmo-audit-service.service" "$D/"
sudo chown -R emqx-monitor:emqx-monitor "$D"
sudo chmod 600 "$D/fmo-audit-service.db" 2>/dev/null || true

# 2. 安装 systemd 服务（按实际路径调整）
sudo sed -i "s|/opt/emqx-monitor|$D|g" "$D/fmo-audit-service.service"
sudo cp "$D/fmo-audit-service.service" /etc/systemd/system/emqx-monitor.service
sudo systemctl daemon-reload
sudo systemctl enable emqx-monitor > /dev/null 2>&1 || true
sudo systemctl restart emqx-monitor

# 3. 验证
sleep 3
if systemctl is-active --quiet emqx-monitor; then
  PORT=$(grep -oP 'EMQX_MONITOR_PORT=\K[0-9]+' "$D/fmo-audit-service.service" | head -1)
  echo "[OK] 服务已启动"
  echo "     访问 http://<本机IP>:${PORT:-9527} → 首次设置管理员账号"
  echo "     数据目录: $D/fmo-audit-service.db"
else
  echo "[!!] 启动失败，查看日志: journalctl -u emqx-monitor -n 30"
fi
