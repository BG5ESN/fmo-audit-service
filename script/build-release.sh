#!/bin/bash
# ============================================================
# EMQX 审计监控工具 — 发布打包脚本
# 用法: bash script/build-release.sh [版本号] [git-tag]
#   版本号默认 1.0.0；传 "tag" 作为第二参数可自动打 git tag
# 产物: dist/
#   emqx-monitor-server-v<版本>-linux-x64.tar.gz   (Linux 部署包)
#   emqx-monitor-server-v<版本>-win-x64.zip        (Windows 部署包)
#   emqx-monitor-server-v<版本>-src.tar.gz         (源码包, git archive)
#   每个包附带 .sha256 校验文件
# ============================================================
set -e
cd "$(dirname "$0")/.."          # 切到项目根目录
VER="${1:-1.0.0}"
DO_TAG="${2:-}"
DIST="dist"
LOG="/tmp/emqx-release-${VER}.log"

# ---- 前置检查 ----
command -v dotnet > /dev/null || { echo "[!] 未找到 dotnet，请先安装 .NET SDK"; exit 1; }
command -v python3 > /dev/null || { echo "[!] 未找到 python3（打包 zip 需要）"; exit 1; }
git diff --quiet || { echo "[!] 工作区有未提交改动，请先 commit 再打包"; exit 1; }

mkdir -p "$DIST"

echo "== [1/5] 发布 linux-x64 单文件 =="
dotnet publish -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/linux-x64-server >> "$LOG" 2>&1

echo "== [2/5] 发布 win-x64 单文件 =="
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/win-x64-server >> "$LOG" 2>&1

echo "== [3/5] 打包部署包 =="
# --- Linux: 二进制 + deploy 脚本 + systemd 模板 + README ---
rm -rf /tmp/emqx-rel-linux && mkdir -p /tmp/emqx-rel-linux
cp publish/linux-x64-server/emqx-monitor-server /tmp/emqx-rel-linux/
cp deploy/emqx-monitor-server.service /tmp/emqx-rel-linux/
cp deploy-linux.sh /tmp/emqx-rel-linux/
cp README.md /tmp/emqx-rel-linux/
tar czf "$DIST/emqx-monitor-server-v${VER}-linux-x64.tar.gz" -C /tmp/emqx-rel-linux .
rm -rf /tmp/emqx-rel-linux

# --- Windows: exe + README ---
rm -rf /tmp/emqx-rel-win && mkdir -p /tmp/emqx-rel-win
cp publish/win-x64-server/emqx-monitor-server.exe /tmp/emqx-rel-win/
cp README.md /tmp/emqx-rel-win/
python3 - "$DIST/emqx-monitor-server-v${VER}-win-x64.zip" <<'EOF'
import sys, zipfile, os
out, src = sys.argv[1], '/tmp/emqx-rel-win'
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, src))
EOF
rm -rf /tmp/emqx-rel-win

echo "== [4/5] 导出源码包 (git archive) =="
git archive --format=tar.gz --prefix="emqx-monitor-server/" \
  -o "$DIST/emqx-monitor-server-v${VER}-src.tar.gz" HEAD

echo "== [5/5] 生成校验和 =="
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
