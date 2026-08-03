#!/bin/bash
# EMQX 审计监控工具 — Linux 一键部署脚本
# 用法: 解压 tar.gz 后 bash deploy-linux.sh [安装目录]
# 默认安装到 /opt/emqx-monitor（systemd 服务 emqx-monitor）
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
D="${1:-/opt/emqx-monitor}"

echo "== EMQX 审计监控工具部署 =="
echo "安装目录: $D"

# 1. 创建安装目录
sudo mkdir -p "$D"
sudo cp "$DIR/emqx-monitor-server" "$D/"
[ -f "$DIR/emqx-monitor-server.service" ] && sudo cp "$DIR/emqx-monitor-server.service" "$D/"

# 2. 安装 systemd 服务（按实际路径调整）
sudo sed -i "s|/opt/emqx-monitor|$D|g" "$D/emqx-monitor-server.service"
sudo cp "$D/emqx-monitor-server.service" /etc/systemd/system/emqx-monitor.service
sudo systemctl daemon-reload
sudo systemctl enable emqx-monitor > /dev/null 2>&1 || true
sudo systemctl restart emqx-monitor

# 3. 验证
sleep 3
if systemctl is-active --quiet emqx-monitor; then
  PORT=$(grep -oP 'EMQX_MONITOR_PORT=\K[0-9]+' "$D/emqx-monitor-server.service" | head -1)
  echo "[OK] 服务已启动"
  echo "     访问 http://<本机IP>:${PORT:-9527} → 首次设置管理员账号"
  echo "     数据目录: $D/emqx-monitor-server.db"
else
  echo "[!!] 启动失败，查看日志: journalctl -u emqx-monitor -n 30"
fi
