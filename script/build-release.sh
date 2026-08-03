#!/bin/bash
# ============================================================
# EMQX 审计监控工具 — 发布打包脚本（多平台）
# 用法: bash script/build-release.sh [版本号] [tag]
#   平台列表可用环境变量覆盖: PLATFORMS=linux-x64,win-x64 bash script/build-release.sh
# 支持平台: linux-x64 / linux-arm64(树莓派64位) / linux-arm(树莓派32位)
#           osx-x64(Intel Mac) / osx-arm64(Apple Silicon) / win-x64
# 产物: dist/
#   emqx-monitor-server-v<版本>-<平台>.tar.gz|.zip  (部署包)
#   emqx-monitor-server-v<版本>-src.tar.gz          (源码包)
#   每个包附带 .sha256 校验文件
# ============================================================
set -e
cd "$(dirname "$0")/.."          # 切到项目根目录
VER="${1:-1.0.0}"
DO_TAG="${2:-}"
PLATFORMS="${PLATFORMS:-linux-x64,linux-arm64,linux-arm,osx-x64,osx-arm64,win-x64}"
DIST="dist"
LOG="/tmp/emqx-release-${VER}.log"

# ---- 前置检查 ----
command -v dotnet > /dev/null || { echo "[!] 未找到 dotnet，请先安装 .NET SDK"; exit 1; }
command -v python3 > /dev/null || { echo "[!] 未找到 python3（打包 zip 需要）"; exit 1; }
git diff --quiet || { echo "[!] 工作区有未提交改动，请先 commit 再打包"; exit 1; }

IFS=',' read -ra PLATS <<< "$PLATFORMS"
mkdir -p "$DIST"

echo "== 发布平台: ${PLATS[*]} =="
echo "（树莓派 32 位系统用 linux-arm，64 位系统用 linux-arm64；macOS 交叉发布需 macOS SDK 支持）"

# ---- 1. 逐平台发布 + 打包 ----
for RID in "${PLATS[@]}"; do
  echo "== [发布] $RID =="
  dotnet publish -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "publish/$RID" >> "$LOG" 2>&1 || { echo "[!] $RID 发布失败，查看 $LOG"; exit 1; }

  STAGE="/tmp/emqx-rel-$RID"
  rm -rf "$STAGE" && mkdir -p "$STAGE"

  case "$RID" in
    win-*)
      # Windows: exe + README
      cp "publish/$RID/emqx-monitor-server.exe" "$STAGE/"
      cp README.md "$STAGE/"
      python3 - "$DIST/emqx-monitor-server-v${VER}-${RID}.zip" <<'EOF'
import sys, zipfile, os
out, src = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, src))
EOF
      rm -rf "$STAGE"
      ;;
    osx-*)
      # macOS: 二进制 + README（mac 无 systemd，直接运行/launchd）
      cp "publish/$RID/emqx-monitor-server" "$STAGE/"
      cp README.md "$STAGE/"
      tar czf "$DIST/emqx-monitor-server-v${VER}-${RID}.tar.gz" -C "$STAGE" .
      rm -rf "$STAGE"
      ;;
    *)
      # Linux 系（x64/arm64/arm）: 二进制 + deploy 脚本 + systemd 模板 + README
      cp "publish/$RID/emqx-monitor-server" "$STAGE/"
      cp deploy/emqx-monitor-server.service "$STAGE/"
      cp deploy-linux.sh "$STAGE/"
      cp README.md "$STAGE/"
      tar czf "$DIST/emqx-monitor-server-v${VER}-${RID}.tar.gz" -C "$STAGE" .
      rm -rf "$STAGE"
      ;;
  esac
  echo "  ✓ $RID 打包完成"
done

# ---- 2. 源码包 ----
echo "== [源码包] git archive =="
git archive --format=tar.gz --prefix="emqx-monitor-server/" \
  -o "$DIST/emqx-monitor-server-v${VER}-src.tar.gz" HEAD

# ---- 3. 校验和 ----
echo "== [校验和] =="
cd "$DIST"
for f in emqx-monitor-server-v${VER}-*; do
  [ -f "$f" ] && sha256sum "$f" > "$f.sha256"
done
cd - > /dev/null

echo ""
echo "== 完成 =="
ls -la "$DIST/" | grep -v "^total"
echo "日志: $LOG"

# ---- 可选: 打 git tag ----
if [ "$DO_TAG" = "tag" ]; then
  git tag "v${VER}" && echo "git tag: v${VER} 已创建（push 时用 git push --tags）"
fi
