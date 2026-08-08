#!/bin/bash
# ============================================================
# FMO审计工具 — 多平台单文件编译工具
# 用法: bash script/build-all.sh [版本号] [tag]
#   版本号默认: 最近 git tag 或 1.0.0
#   平台列表可用环境变量覆盖: PLATFORMS=linux-x64,win-x64 bash script/build-all.sh
# 支持平台: linux-x64 / linux-arm64(树莓派64) / linux-arm(树莓派32)
#           osx-x64(Intel Mac) / osx-arm64(Apple Silicon) / win-x64
# 产物: dist/ 下每个平台一个最终单文件 + .sha256 校验
#   fmo-audit-service-<平台>.tar.gz/.zip（固定名，版本由元数据 version 表达）
# 版本号必须是干净的语义版本 (x.y.z)：自动写入 csproj <Version>（程序集版本 = 发布版本，
#   OTA 的 CurrentVersion 读程序集版本，不 bump 会导致永远"有新版本"/永远"已最新"）
# 可选: 第二个参数 tag → 打 git tag v<版本>
# ============================================================
set -e
cd "$(dirname "$0")/.."          # 切到项目根目录
OUTBASE="$(pwd)/publish"          # 绝对路径: 防止在发布目录内运行导致输出嵌套

# ---- 清空构建产物: 防止残留 publish 目录被重复拷贝嵌套(曾导致 bin/.../publish/.../publish 无限加深) ----
rm -rf bin obj publish

# ---- 版本 / 平台 / 路径 ----
VER="${1:-$(git describe --tags --always 2>/dev/null || echo 1.0.0)}"
VER="${VER#v}"          # 去掉 v 前缀（git describe 返回 v2.0.2）
DO_TAG="${2:-}"
PLATFORMS="${PLATFORMS:-linux-x64,linux-arm64,linux-arm,osx-x64,osx-arm64,win-x64}"
DIST="dist"
LOG="/tmp/fmo-build-all-$(date +%s).log"

# ---- 版本校验：必须是干净语义版本（x.y.z），强制"先打 tag 再发布" ----
# 脏版本（如 2.0.12-1-g91d0baf）写进 csproj 会导致 AssemblyVersion 非法编译失败，
# 且元数据 version 会被 CompareVersions 解析错位——直接拒绝
if ! [[ "$VER" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "[!] 版本号必须是干净的语义版本 (x.y.z)，当前: $VER"
  echo "    请先打 tag: git tag v<版本> && git push --tags，或显式传参: bash script/build-all.sh 2.0.13"
  exit 1
fi

# ---- 前置检查 ----
command -v dotnet >/dev/null 2>&1 || { echo "[!] 未找到 dotnet，请先安装 .NET SDK"; exit 1; }
git diff --quiet || { echo "[!] 工作区有未提交改动，请先 commit 再编译（发布必须可追溯）"; exit 1; }

# ---- 版本写入 csproj：程序集版本 = 发布版本（OTA CurrentVersion 读程序集版本）----
if ! grep -q "<Version>${VER}</Version>" emqx-monitor.csproj; then
  sed -i "s|<Version>[^<]*</Version>|<Version>${VER}</Version>|" emqx-monitor.csproj
  echo "== 版本已写入 csproj: $VER（请随发布一起提交）=="
else
  echo "== csproj 版本已是 $VER，无需更新 =="
fi

mkdir -p "$DIST"
# 清理旧产物：产物带版本号会累积（每版 6 平台约 600MB），发布前清空
rm -rf "$DIST"/* 2>/dev/null || true
echo "== FMO Audit Service 多平台单文件编译 v$VER =="
echo "平台: ${PLATFORMS//,/, }"
echo "（树莓派 32 位用 linux-arm，64 位用 linux-arm64）"

# ---- 逐平台 publish（单文件自包含）+ 打包（tar.gz/zip，对齐 SAS）----
for RID in ${PLATFORMS//,/ }; do
  echo "== [$RID] dotnet publish 单文件 =="
  dotnet publish -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None -p:DebugSymbols=false \
    -o "$OUTBASE/$RID" >> "$LOG" 2>&1 || { echo "[!] $RID 发布失败，日志: $LOG"; exit 1; }

  BIN="publish/$RID/fmo-audit-service"
  [ "$RID" = "win-x64" ] && BIN="$BIN.exe"
  [ -f "$BIN" ] || { echo "[!] $RID 产物缺失: $BIN，日志: $LOG"; exit 1; }

  # 打包（Linux/macOS → tar.gz，Windows → zip），本地固定名
  STAGE="/tmp/fas-rel-$RID"
  rm -rf "$STAGE" && mkdir -p "$STAGE"
  cp "$BIN" "$STAGE/"
  if [ "$RID" = "win-x64" ]; then
    OUT="$DIST/fmo-audit-service-${RID}.zip"
    python3 - "$OUT" "$STAGE" <<'EOF'
import sys, zipfile, os
out, src = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, src))
EOF
  else
    OUT="$DIST/fmo-audit-service-${RID}.tar.gz"
    tar czf "$OUT" -C "$STAGE" .
  fi
  rm -rf "$STAGE"
  sha256sum "$OUT" > "$OUT.sha256"
  echo "  ✓ $OUT ($(du -h "$OUT" | cut -f1))"
done

echo ""
echo "== 完成 =="
ls -la "$DIST/" | grep -v "^total"
echo "日志: $LOG"
echo "提示: 提交版本号变更 → git add emqx-monitor.csproj && git commit -m \"bump: v$VER\""

# ---- 可选: 打 git tag ----
if [ "$DO_TAG" = "tag" ]; then
  git tag "v${VER}" && echo "git tag: v${VER} 已创建（推送: git push --tags）"
fi
