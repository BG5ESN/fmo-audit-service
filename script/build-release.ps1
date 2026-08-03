# ============================================================
# EMQX 审计监控工具 — 发布打包脚本 (Windows PowerShell)
# 用法: powershell -ExecutionPolicy Bypass -File script\build-release.ps1 [-Version 1.0.0] [-Tag]
#   需 Windows 10 1803+（自带 tar 命令）；dotnet SDK + git 必需
# 产物: dist/
#   emqx-monitor-server-v<版本>-linux-x64.tar.gz   (Linux 部署包)
#   emqx-monitor-server-v<版本>-win-x64.zip        (Windows 部署包)
#   emqx-monitor-server-v<版本>-src.tar.gz         (源码包, git archive)
#   每个包附带 .sha256 校验文件
# ============================================================
param(
    [string]$Version = "1.0.0",
    [switch]$Tag
)
$ErrorActionPreference = "Stop"
$ProjRoot = Split-Path $PSScriptRoot -Parent
Set-Location $ProjRoot

# ---- 前置检查 ----
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Host "[!] 未找到 dotnet，请先安装 .NET SDK"; exit 1 }
if (-not (Get-Command git -ErrorAction SilentlyContinue))     { Write-Host "[!] 未找到 git"; exit 1 }
if (-not (Get-Command tar -ErrorAction SilentlyContinue))     { Write-Host "[!] 未找到 tar（需 Windows 10 1803+）"; exit 1 }

$status = git status --porcelain
if ($status) { Write-Host "[!] 工作区有未提交改动，请先 commit 再打包"; exit 1 }

$Dist = "dist"
New-Item -ItemType Directory -Force -Path $Dist | Out-Null
$Log = Join-Path $env:TEMP "emqx-release-$Version.log"
$tmp = Join-Path $env:TEMP "emqx-rel-$Version"
$relLinux = Join-Path $tmp "linux"
$relWin   = Join-Path $tmp "win"

Write-Host "== [1/5] 发布 linux-x64 单文件 =="
dotnet publish -c Release -r linux-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/linux-x64-server *>> $Log

Write-Host "== [2/5] 发布 win-x64 单文件 =="
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/win-x64-server *>> $Log

Write-Host "== [3/5] 打包部署包 =="
# --- Linux: 二进制 + deploy 脚本 + systemd 模板 + README ---
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
New-Item -ItemType Directory -Force -Path $relLinux | Out-Null
Copy-Item publish/linux-x64-server/emqx-monitor-server $relLinux/
Copy-Item deploy/emqx-monitor-server.service $relLinux/
Copy-Item deploy-linux.sh $relLinux/
Copy-Item README.md $relLinux/
tar czf "$Dist/emqx-monitor-server-v$Version-linux-x64.tar.gz" -C $relLinux .

# --- Windows: exe + README ---
New-Item -ItemType Directory -Force -Path $relWin | Out-Null
Copy-Item publish/win-x64-server/emqx-monitor-server.exe $relWin/
Copy-Item README.md $relWin/
Compress-Archive -Path (Join-Path $relWin "*") -DestinationPath "$Dist/emqx-monitor-server-v$Version-win-x64.zip" -Force

Write-Host "== [4/5] 导出源码包 (git archive) =="
git archive --format=tar.gz --prefix="emqx-monitor-server/" `
  -o "$Dist/emqx-monitor-server-v$Version-src.tar.gz" HEAD

Write-Host "== [5/5] 生成校验和 =="
Get-ChildItem "$Dist/emqx-monitor-server-v$Version-*" -File | Where-Object { $_.Extension -ne ".sha256" } | ForEach-Object {
    $hash = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLower()
    "$hash  $($_.Name)" | Out-File -FilePath "$($_.FullName).sha256" -Encoding ascii
}

Remove-Item -Recurse -Force $tmp
Write-Host ""
Write-Host "== 完成 =="
Get-ChildItem $Dist | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "日志: $Log"

if ($Tag) {
    git tag "v$Version"
    Write-Host "git tag: v$Version 已创建（push 时用 git push --tags）"
}
