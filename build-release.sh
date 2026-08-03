#!/bin/bash
# EMQX 审计监控工具 — 发布打包脚本
# 用法: bash build-release.sh [版本号]
# 产物: dist/emqx-monitor-server-v<版本>-linux-x64.tar.gz + -win-x64.zip
set -e
cd "$(dirname "$0")"
VER="${1:-1.0.0}"
DIST="dist"
mkdir -p "$DIST"

echo "== 1/3 编译发布 linux-x64 =="
dotnet publish -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/linux-x64-server > /dev/null 2>&1

echo "== 2/3 编译发布 win-x64 =="
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/win-x64-server > /dev/null 2>&1

echo "== 3/3 打包 =="
# Linux: 二进制 + deploy 脚本 + 服务模板
rm -rf /tmp/emqx-rel-linux && mkdir -p /tmp/emqx-rel-linux
cp publish/linux-x64-server/emqx-monitor-server /tmp/emqx-rel-linux/
cp deploy/emqx-monitor-server.service /tmp/emqx-rel-linux/
cp deploy-linux.sh /tmp/emqx-rel-linux/
cp README.md /tmp/emqx-rel-linux/ 2>/dev/null || true
tar czf "$DIST/emqx-monitor-server-v${VER}-linux-x64.tar.gz" -C /tmp/emqx-rel-linux .
rm -rf /tmp/emqx-rel-linux

# Windows: exe + 部署说明
rm -rf /tmp/emqx-rel-win && mkdir -p /tmp/emqx-rel-win
cp publish/win-x64-server/emqx-monitor-server.exe /tmp/emqx-rel-win/
cp README.md /tmp/emqx-rel-win/ 2>/dev/null || true
python3 -c "
import zipfile, os
with zipfile.ZipFile('$DIST/emqx-monitor-server-v${VER}-win-x64.zip', 'w', zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk('/tmp/emqx-rel-win'):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, '/tmp/emqx-rel-win'))
"
rm -rf /tmp/emqx-rel-win

ls -la "$DIST/"
echo "== 完成: dist/emqx-monitor-server-v${VER}-* =="
