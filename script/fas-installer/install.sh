#!/bin/bash
# ============================================================
# FMO Audit Service (FAS) — Linux 一键安装脚本
# 用法: curl -fsSL https://bg5esn.com/share/fmo/fas-installer/install.sh | sudo bash
# 安装: /opt/fmo-fas/fmo-audit-service + systemd 服务 fmo-fas
# 配置: 安装后浏览器访问 http://<IP>:9527 首次设置管理员并连接 EMQX
# 升级: 页面「版本与更新」按钮 或 fmo-audit-service --update
# ============================================================
set -e

META_URL="https://bg5esn.com/share/fmo/fas.json"
SCRIPT_VERSION="1.0.0"
INSTALL_DIR="/opt/fmo-fas"
CACHE_DIR="/var/cache/fmo-fas"
SERVICE_NAME="fmo-fas"
RUN_USER="fmo-audit"
TMPDIR_INSTALL=""

cleanup() { [ -n "$TMPDIR_INSTALL" ] && rm -rf "$TMPDIR_INSTALL"; }
trap cleanup EXIT

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info() { printf "${CYAN}%s${NC}\n" "$*"; }
ok()   { printf "${GREEN}%s${NC}\n" "$*"; }
warn() { printf "${YELLOW}%s${NC}\n" "$*"; }
err()  { printf "${RED}%s${NC}\n" "$*"; }

# ═══════════════════════════════════════════════════════════════
info "═══ FMO Audit Service Installer v${SCRIPT_VERSION} ═══"

# ── Root 检查 ──
if [ "$(id -u)" -ne 0 ]; then
    err "请以 root 权限运行此脚本"
    err "用法: curl -fsSL https://bg5esn.com/share/fmo/fas-installer/install.sh | sudo bash"
    exit 1
fi

# ── 依赖预检 ──
MISSING=""
for cmd in curl tar mktemp; do
    if ! command -v "$cmd" >/dev/null 2>&1; then
        MISSING="$MISSING $cmd"
    fi
done
if ! command -v systemctl >/dev/null 2>&1; then
    MISSING="$MISSING systemctl(systemd)"
fi
if [ -n "$MISSING" ]; then
    err "当前环境缺少:${MISSING}，请安装后重试"
    exit 1
fi

# ═══════════════════════════════════════════════════════════════
# STEP 1: 平台检测
# ═══════════════════════════════════════════════════════════════
OS=$(uname -s); ARCH=$(uname -m)
case "$OS-$ARCH" in
    Linux-x86_64)  RID="linux-x64" ;;
    Linux-aarch64) RID="linux-arm64" ;;
    Linux-armv7l|Linux-armv6l) RID="linux-arm" ;;   # 树莓派 32 位系统
    *)
        err "不支持的平台: $OS $ARCH（支持 linux-x64 / linux-arm64 / linux-arm）"
        exit 1 ;;
esac
info "[1/5] 检测平台... $OS $ARCH -> $RID"

# ═══════════════════════════════════════════════════════════════
# STEP 2: 获取版本 + 下载
# ═══════════════════════════════════════════════════════════════
info "[2/5] 获取最新版本..."
META=$(curl -fsSL "$META_URL")
VERSION=$(echo "$META" | sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' | head -1)
DOWNLOAD_URL=$(echo "$META" | sed -n "s/.*\"$RID\": *\"\([^\"]*\)\".*/\1/p" | head -1)

if [ -z "$VERSION" ] || [ -z "$DOWNLOAD_URL" ]; then
    err "无法解析版本信息（$META_URL），请检查网络或元数据是否已发布"
    exit 1
fi
info "      版本: v$VERSION | 下载: $DOWNLOAD_URL"

TMPDIR_INSTALL=$(mktemp -d)
ARCHIVE="$TMPDIR_INSTALL/fas.tar.gz"
# 传输完整性由 HTTPS/TLS 保障，官方源可信，无需额外哈希
curl -fL --progress-bar "$DOWNLOAD_URL" -o "$ARCHIVE"

mkdir -p "$TMPDIR_INSTALL/extract"
# 下载的是 tar.gz 包（build-all 产物，对齐 SAS），解压找主程序
tar xzf "$ARCHIVE" -C "$TMPDIR_INSTALL/extract"
BIN_FILE="$TMPDIR_INSTALL/extract/fmo-audit-service"
# 部分平台打出的 tar 不带可执行位，此处只校验存在性，安装时统一 chmod +x
if [ ! -f "$BIN_FILE" ]; then
    err "下载文件解压异常，未找到 fmo-audit-service 主程序"
    err "您可以尝试安装组件后重试，或手动部署，参考: https://bg5esn.com/docs/fmo-fas-install-guide/"
    exit 1
fi

# ═══════════════════════════════════════════════════════════════
# STEP 3: 安装
# ═══════════════════════════════════════════════════════════════
info "[3/5] 安装到 $INSTALL_DIR ..."
if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    systemctl stop "$SERVICE_NAME"
    warn "      已停止旧版 $SERVICE_NAME 服务"
fi
pkill -f "$INSTALL_DIR/fmo-audit-service" 2>/dev/null || true
sleep 1

mkdir -p "$INSTALL_DIR"
if ! id "$RUN_USER" >/dev/null 2>&1; then
    useradd --system --no-create-home --home-dir "$INSTALL_DIR" --shell /usr/sbin/nologin "$RUN_USER"
fi
cp "$BIN_FILE" "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/fmo-audit-service"
chown -R "$RUN_USER:$RUN_USER" "$INSTALL_DIR"
# 单文件自解压缓存目录（ProtectSystem=strict 下二进制目录只读，解压必须落到这里）
mkdir -p "$CACHE_DIR"
chown "$RUN_USER:$RUN_USER" "$CACHE_DIR"
rm -rf "$TMPDIR_INSTALL"
ok "      完成"

# ═══════════════════════════════════════════════════════════════
# STEP 4: 注册 systemd 服务
# ═══════════════════════════════════════════════════════════════
info "[4/5] 注册系统服务..."
cat > /etc/systemd/system/${SERVICE_NAME}.service << EOF
[Unit]
Description=FMO Audit Service (审计监控)
After=network.target

[Service]
Type=simple
User=$RUN_USER
Group=$RUN_USER
ExecStart=$INSTALL_DIR/fmo-audit-service
WorkingDirectory=$INSTALL_DIR
Environment=EMQX_MONITOR_PORT=9527
Environment=EMQX_MONITOR_DB=$INSTALL_DIR/fmo-audit-service.db
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=$CACHE_DIR
# on-failure + 自动重启：OTA 更新时进程退出即自动拉起新版本
Restart=on-failure
RestartSec=5

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
# strict 下除 API 文件系统外全局只读；放行安装目录（db 写入 + OTA 二进制替换），
# 单文件解压缓存目录同样必须显式可写（DOTNET_BUNDLE_EXTRACT_BASE_DIR）
ReadWritePaths=$INSTALL_DIR $CACHE_DIR
ProtectHome=true

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE_NAME" > /dev/null 2>&1
START_TIME=$(date '+%Y-%m-%d %H:%M:%S')
systemctl start "$SERVICE_NAME"
info "      等待服务启动 (5 秒)..."
sleep 5
echo ""
echo "  ── 启动日志 ──"
journalctl -u "$SERVICE_NAME" --since "$START_TIME" --no-pager 2>/dev/null | tail -15 || true
echo "  ──────────────"

# ═══════════════════════════════════════════════════════════════
# STEP 5: 验证 + 使用说明
# ═══════════════════════════════════════════════════════════════
info "[5/5] 验证服务..."
if systemctl is-active --quiet "$SERVICE_NAME"; then
    PORT=$(grep -oP 'EMQX_MONITOR_PORT=\K[0-9]+' /etc/systemd/system/${SERVICE_NAME}.service | head -1)
    IP=$(hostname -I 2>/dev/null | awk '{print $1}')
    ok "═══ FMO Audit Service v$VERSION 安装完成并已启动 ═══"
    echo ""
    echo "  访问:     http://${IP:-<本机IP>}:${PORT:-9527}"
    echo "  首次使用: 设置管理员账号 → 配置页填入 EMQX 地址 + API 密钥 → 启用主题统计"
    echo ""
    echo "  日志:     journalctl -u $SERVICE_NAME -f"
    echo "  状态:     systemctl status $SERVICE_NAME"
    echo "  升级:     页面「版本与更新」按钮，或 $INSTALL_DIR/fmo-audit-service --update"
    echo "  卸载:     curl -fsSL https://bg5esn.com/share/fmo/fas-installer/uninstall.sh | sudo bash"
    echo ""
    echo "  数据目录: $INSTALL_DIR/（fmo-audit-service.db，备份请复制该文件）"
else
    err "═══ 安装完成但服务启动失败，查看日志: journalctl -u $SERVICE_NAME -n 30 ═══"
    exit 1
fi
