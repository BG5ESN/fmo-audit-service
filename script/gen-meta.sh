#!/bin/bash
# ============================================================
# FMO Audit Service — OTA 元数据生成工具（对齐 SAS sas.json 格式）
# 用法:
#   bash script/gen-meta.sh [版本] [基础URL] [发布说明]   # 非交互（脚本化）
#   bash script/gen-meta.sh                               # 交互模式（终端下逐项询问）
#   bash script/gen-meta.sh -h | --help                   # 帮助
# 版本直接取 git 最近 tag（发布版本 = tag，与 build-all 一致；无 tag 拒绝生成）
# 基础URL 默认 https://bg5esn.com/share/fmo/fmo-audit-service/
# 发布说明可选（如 "BUG FIX."），写入 notes 字段
# 扫描 dist/ 下 fmo-audit-service-<rid>.tar.gz|.zip 产物（固定名），
# 生成 fas.json —— URL 带版本目录，与 SAS 同风格
# 上传: 产物 → <基础URL>/v<版本>/，元数据 → <基础URL>/
# ============================================================
set -e
cd "$(dirname "$0")/.."

show_help() {
  cat <<'EOF'
FMO Audit Service - OTA 元数据生成工具

用法:
  bash script/gen-meta.sh [版本] [基础URL] [发布说明]   非交互（脚本化）
  bash script/gen-meta.sh                               交互模式（终端下逐项询问）
  bash script/gen-meta.sh -h | --help                   显示本帮助

参数:
  版本      默认取 git 最近 tag（如 2.0.8），无 tag 则 0.0.0
  基础URL   默认 https://bg5esn.com/share/fmo/fmo-audit-service/
  发布说明  可选（如 "BUG FIX."），写入 notes 字段

示例:
  bash script/gen-meta.sh
  bash script/gen-meta.sh 2.0.13 "BUG FIX."
  bash script/gen-meta.sh 2.0.13 https://example.com/share/ "修复xxx"

说明: 扫描 dist/ 下 fmo-audit-service-<rid>.tar.gz/.zip 产物，生成 fmo-audit-service.json；
      上传: 产物 → <基础URL>/v<版本>/，元数据 → <基础URL>/ (覆盖)
EOF
}

# ---- 帮助 ----
for a in "$@"; do
  case "$a" in
    -h|--help|help) show_help; exit 0 ;;
  esac
done

# ---- 版本：取最近 git tag（--abbrev=0 只取 tag 名，不带提交数/哈希脏后缀）----
DEFAULT_VER="$(git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//')"
if [ -z "$DEFAULT_VER" ]; then
  echo "[!] 未找到 git tag，请先打 tag（发布版本必须可追溯，与 build-all 一致）: git tag v<版本>" >&2
  exit 1
fi

# ---- 参数解析：无参数 + 终端 → 交互模式；无参数 + 非终端 → 拒绝（防后台卡死）----
if [ $# -eq 0 ]; then
  if [ -t 0 ]; then
    echo "== FMO Audit Service 元数据生成（交互模式）=="
    VER="$DEFAULT_VER"   # 版本直接用 git tag，不再询问（与 build-all 的发布版本一致）
    echo "版本（git tag）: $VER"
    read -rp "基础 URL [https://bg5esn.com/share/fmo/fmo-audit-service/]: " BASE
    BASE="${BASE:-https://bg5esn.com/share/fmo/fmo-audit-service/}"
    read -rp "发布说明（可选，直接回车跳过）: " NOTES
  else
    echo "[!] 交互模式需要终端。非交互请传参: bash script/gen-meta.sh [版本] [基础URL] [发布说明]" >&2
    echo "    帮助: bash script/gen-meta.sh -h" >&2
    exit 1
  fi
else
  VER="${1:-$DEFAULT_VER}"
  BASE="${2:-https://bg5esn.com/share/fmo/fmo-audit-service/}"
  NOTES="${3:-}"
fi

DIST="dist"
OUT="fas.json"

echo "== FMO Audit Service 元数据生成 v$VER =="
echo "基础 URL: $BASE"
[ -n "$NOTES" ] && echo "发布说明: $NOTES"

# 扫描产物：fmo-audit-service-<rid>.tar.gz / .zip（固定名，对齐 SAS）
RIDS="linux-x64 linux-arm64 linux-arm osx-x64 osx-arm64 win-x64"
ASSETS=""
COUNT=0
for RID in $RIDS; do
  for f in "$DIST"/fmo-audit-service-${RID}.tar.gz "$DIST"/fmo-audit-service-${RID}.zip; do
    [ -f "$f" ] || continue
    NAME=$(basename "$f")
    # URL 带版本目录：<base>/v<版本>/<固定名>（对齐 SAS sas/v1.0.5/xxx）
    ASSETS="$ASSETS
    \"$RID\": \"${BASE}v${VER}/${NAME}\","
    COUNT=$((COUNT + 1))
    echo "  + $NAME -> rid=$RID (v${VER}/)"
  done
done

if [ "$COUNT" -eq 0 ]; then
  echo "[!] dist/ 下没有找到产物（fmo-audit-service-<rid>.tar.gz/.zip），请先运行 build-all.sh"
  exit 1
fi

# 去掉最后一个逗号
ASSETS="${ASSETS%,}"
{
  echo "{"
  echo "  \"version\": \"$VER\","
  [ -n "$NOTES" ] && echo "  \"notes\": \"$NOTES\","
  echo "  \"url\": \"https://github.com/BG5ESN/fmo-server-authrozier-service\","
  echo "  \"assets\": {$ASSETS"
  echo "  }"
  echo "}"
} > "$OUT"

echo ""
echo "== 已生成 $OUT =="
cat "$OUT"
echo ""
echo "上传步骤:"
echo "  1. 产物 → ${BASE}v${VER}/"
echo "  2. 元数据 → ${BASE}fas.json（覆盖）"
