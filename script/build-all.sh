#!/bin/bash
# ============================================================
# FMO审计工具 — 多平台单文件编译工具
# 用法: bash script/build-all.sh [版本号] [tag]
#   版本号默认: 最近 git tag 或 1.0.0
#   平台列表可用环境变量覆盖: PLATFORMS=linux-x64,win-x64 bash script/build-all.sh
# 支持平台: linux-x64 / linux-arm64(树莓派64) / linux-arm(树莓派32)
#           osx-x64(Intel Mac) / osx-arm64(Apple Silicon) / win-x64
# 产物: dist/ 下每个平台一个最终单文件 + .sha256 校验
#   fmo-audit-service-<平台>-v<版本>    (win 为 .exe)
# 可选: 第二个参数 tag → 打 git tag v<版本>
# ============================================================
set -e
cd "$(dirname "$0")/.."          # 切到项目根目录

# ---- 版本 / 平台 / 路径 ----
VER="${1:-$(git describe --tags --always 2>/dev/null || echo 1.0.0)}"
VER="${VER#v}"          # 去掉 v 前缀（git describe 返回 v2.0.2）
DO_TAG="${2:-}"
PLATFORMS="${PLATFORMS:-linux-x64,linux-arm64,linux-arm,osx-x64,osx-arm64,win-x64}"
DIST="dist"
LOG="/tmp/fmo-build-all-$(date +%s).log"

# ---- 前置检查 ----
command -v dotnet >/dev/null 2>&1 || { echo "[!] 未找到 dotnet，请先安装 .NET SDK"; exit 1; }
git diff --quiet || { echo "[!] 工作区有未提交改动，请先 commit 再编译（发布必须可追溯）"; exit 1; }

mkdir -p "$DIST"
# 清理旧产物：产物带版本号会累积（每版 6 平台约 600MB），发布前清空
rm -rf "$DIST"/* 2>/dev/null || true
echo "== FMO Audit Service 多平台单文件编译 v$VER =="
echo "平台: ${PLATFORMS//,/, }"
echo "（树莓派 32 位用 linux-arm，64 位用 linux-arm64）"

# ---- 逐平台 publish（单文件自包含）----
for RID in ${PLATFORMS//,/ }; do
  echo "== [$RID] dotnet publish 单文件 =="
  dotnet publish -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None -p:DebugSymbols=false \
    -o "publish/$RID" >> "$LOG" 2>&1 || { echo "[!] $RID 发布失败，日志: $LOG"; exit 1; }

  BIN="publish/$RID/fmo-audit-service"
  [ "$RID" = "win-x64" ] && BIN="$BIN.exe"
  [ -f "$BIN" ] || { echo "[!] $RID 产物缺失: $BIN，日志: $LOG"; exit 1; }

  OUT="$DIST/fmo-audit-service-${RID}"
  cp "$BIN" "$OUT"
  chmod +x "$OUT" 2>/dev/null || true
  sha256sum "$OUT" > "$OUT.sha256"
  echo "  ✓ $OUT ($(du -h "$OUT" | cut -f1))"
done

echo ""
echo "== 完成 =="
ls -la "$DIST/" | grep -v "^total"
echo "日志: $LOG"

# ---- 可选: 打 git tag ----
if [ "$DO_TAG" = "tag" ]; then
  git tag "v${VER}" && echo "git tag: v${VER} 已创建（推送: git push --tags）"
fi
