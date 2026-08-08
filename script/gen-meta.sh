#!/bin/bash
# ============================================================
# FMO Audit Service — OTA 元数据生成工具（对齐 SAS sas.json 格式）
# 用法: bash script/gen-meta.sh [版本] [基础URL] [发布说明]
#   版本默认取 git 最近 tag（如 v2.0.8）
#   基础URL 默认 https://bg5esn.com/share/fmo/fmo-audit-service/
#   发布说明可选（如 "BUG FIX."），写入 notes 字段
# 扫描 dist/ 下 fmo-audit-service-<rid>.tar.gz|.zip 产物（固定名），
# 生成 fmo-audit-service.json —— URL 带版本目录，与 SAS 同风格
# 上传: 产物 → <基础URL>/v<版本>/，元数据 → <基础URL>/
# ============================================================
set -e
cd "$(dirname "$0")/.."

VER="${1:-$(git describe --tags --always 2>/dev/null | sed 's/^v//' || echo 0.0.0)}"
BASE="${2:-https://bg5esn.com/share/fmo/fmo-audit-service/}"
NOTES="${3:-}"
DIST="dist"
OUT="fmo-audit-service.json"

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
echo "  2. 元数据 → ${BASE}fmo-audit-service.json（覆盖）"
