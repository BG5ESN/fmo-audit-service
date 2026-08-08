#!/bin/bash
# ============================================================
# FMO Audit Service (FAS) — Linux 卸载脚本
# 用法: curl -fsSL https://bg5esn.com/share/fmo/fas-installer/uninstall.sh | sudo bash
# ============================================================
set -e
INSTALL_DIR="/opt/fmo-fas"
SERVICE_NAME="fmo-fas"
RUN_USER="fmo-audit"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
err()  { printf "${RED}%s${NC}\n" "$*"; }
ok()   { printf "${GREEN}%s${NC}\n" "$*"; }
warn() { printf "${YELLOW}%s${NC}\n" "$*"; }

if [ "$(id -u)" -ne 0 ]; then
    err "请以 root 权限运行"
    exit 1
fi

echo "== FMO Audit Service 卸载 =="

# 停止并移除服务
if systemctl list-unit-files 2>/dev/null | grep -q "^${SERVICE_NAME}\.service"; then
    systemctl stop "$SERVICE_NAME" 2>/dev/null || true
    systemctl disable "$SERVICE_NAME" 2>/dev/null || true
    rm -f "/etc/systemd/system/${SERVICE_NAME}.service"
    systemctl daemon-reload
    ok "已移除 systemd 服务 $SERVICE_NAME"
fi

# 删除数据？（默认保留，提示用户）
if [ -d "$INSTALL_DIR" ]; then
    read -r -p "删除数据目录 $INSTALL_DIR（含数据库与配置）？[y/N] " ans < /dev/tty
    case "$ans" in
        y|Y)
            rm -rf "$INSTALL_DIR"
            ok "已删除 $INSTALL_DIR"
            ;;
        *)
            warn "保留 $INSTALL_DIR（如需彻底删除请手动 rm -rf $INSTALL_DIR）"
            ;;
    esac
fi

# 删除专用用户
if id "$RUN_USER" >/dev/null 2>&1; then
    userdel "$RUN_USER" 2>/dev/null && ok "已删除用户 $RUN_USER" || warn "用户 $RUN_USER 删除失败（可能仍有进程占用）"
fi

ok "卸载完成"
