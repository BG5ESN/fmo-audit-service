# ============================================================
# FMO审计工具 — 多平台单文件编译工具 (Windows PowerShell)
# 用法: powershell -ExecutionPolicy Bypass -File script\build-all.ps1 [-Version 1.0.0] [-Tag]
#   版本号默认: 最近 git tag 或 1.0.0
#   平台列表可用参数覆盖: -Platforms "linux-x64,win-x64"
# 支持平台: linux-x64 / linux-arm64 / linux-arm / osx-x64 / osx-arm64 / win-x64
# 产物: dist\ 下每个平台一个最终单文件 + .sha256 校验
#   emqx-monitor-server-<平台>-v<版本>    (win 为 .exe)
# 可选: -Tag → 打 git tag v<版本>
# ============================================================
param(
    [string]$Version = "",
    [switch]$Tag,
    [string]$Platforms = "linux-x64,linux-arm64,linux-arm,osx-x64,osx-arm64,win-x64"
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")    # 切到项目根目录

# ---- 版本 ----
if (-not $Version) {
    $desc = & git describe --tags --always 2>$null
    $Version = if ($desc) { $desc } else { "1.0.0" }
}
$Dist = "dist"
$Log = Join-Path $env:TEMP "fmo-build-all-$([DateTime]::Now.Ticks).log"

# ---- 前置检查 ----
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "[!] 未找到 dotnet，请先安装 .NET SDK" -ForegroundColor Red
    exit 1
}
$dirty = & git status --porcelain 2>$null
if ($dirty) {
    Write-Host "[!] 工作区有未提交改动，请先 commit 再编译（发布必须可追溯）" -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Force -Path $Dist | Out-Null
Write-Host "== FMO审计工具 多平台单文件编译 v$Version ==" -ForegroundColor Cyan
Write-Host "平台: $($Platforms -replace ',', ', ')"

# ---- 逐平台 publish（单文件自包含）----
foreach ($RID in $Platforms.Split(',')) {
    Write-Host "== [$RID] dotnet publish 单文件 =="
    & dotnet publish -c Release -r $RID --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false `
        -o "publish/$RID" *>> $Log
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[!] $RID 发布失败，日志: $Log" -ForegroundColor Red
        exit 1
    }

    $Bin = "publish/$RID/emqx-monitor-server"
    if ($RID -eq "win-x64") { $Bin = "$Bin.exe" }
    if (-not (Test-Path $Bin)) {
        Write-Host "[!] $RID 产物缺失: $Bin，日志: $Log" -ForegroundColor Red
        exit 1
    }

    $Out = "$Dist/emqx-monitor-server-$RID-v$Version"
    Copy-Item $Bin $Out -Force
    $Hash = (Get-FileHash $Out -Algorithm SHA256).Hash.ToLower()
    Set-Content -Path "$Out.sha256" -Value "$Hash  $Out" -Encoding ASCII
    $Size = [Math]::Round((Get-Item $Out).Length / 1MB, 1)
    Write-Host "  [OK] $Out ($Size MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "== 完成 ==" -ForegroundColor Cyan
Get-ChildItem $Dist | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host "日志: $Log"

# ---- 可选: 打 git tag ----
if ($Tag) {
    & git tag "v$Version"
    Write-Host "git tag: v$Version 已创建（推送: git push --tags）"
}
