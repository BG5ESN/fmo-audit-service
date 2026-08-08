#!/bin/bash
# ============================================================
# FMO Audit Service — OTA 元数据生成工具
# 用法: bash script/gen-meta.sh [版本] [基础URL]
#   版本默认取 git 最近 tag（如 v2.0.0）
#   基础URL 默认 https://bg5esn.com/share/fmo/
# 扫描 dist/ 下 fmo-audit-service-<rid>-v<ver> 产物，生成 fmo-audit-service.json
# 产物上传后，服务器端 --update / 页面按钮即可发现新版本
# ============================================================
set -e
cd "$(dirname "$0")/.."

VER="${1:-$(git describe --tags --always 2>/dev/null | sed 's/^v//' || echo 0.0.0)}"
BASE="${2:-https://bg5esn.com/share/fmo/}"
DIST="dist"
OUT="fmo-audit-service.json"

echo "== FMO Audit Service 元数据生成 v$VER =="
echo "基础 URL: $BASE"

# 扫描产物：fmo-audit-service-<rid>[.exe]（固定名不带版本，版本由元数据 version 字段表达）
# 注意: rid 含 '-'（如 linux-arm64），用已知平台列表精确匹配
RIDS="linux-x64 linux-arm64 linux-arm osx-x64 osx-arm64 win-x64"
ASSETS=""
COUNT=0
for RID in $RIDS; do
  for f in "$DIST"/fmo-audit-service-${RID}*; do
    [ -f "$f" ] || continue
    case "$f" in *.sha256) continue ;; esac   # 跳过校验文件
    NAME=$(basename "$f")
    ASSETS="$ASSETS
    \"$RID\": \"${BASE}${NAME}\","
    COUNT=$((COUNT + 1))
    echo "  + $NAME -> rid=$RID"
  done
done

if [ "$COUNT" -eq 0 ]; then
  echo "[!] dist/ 下没有找到 v$VER 的产物，请先运行 build-all.sh"
  exit 1
fi

# 去掉最后一个逗号
ASSETS="${ASSETS%,}"
cat > "$OUT" <<EOF
{
  "version": "$VER",
  "assets": {$ASSETS
  }
}
EOF

echo ""
echo "== 已生成 $OUT =="
cat "$OUT"
echo ""
echo "上传 $OUT 与 dist/ 下产物到 $BASE，服务器即可自动发现新版本。"
