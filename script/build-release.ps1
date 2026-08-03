# ============================================================
# EMQX 审计监控工具 — 发布打包脚本 (Windows PowerShell, 多平台)
# 用法: powershell -ExecutionPolicy Bypass -File script\build-release.ps1 [-Version 1.0.0] [-Tag]
#   平台列表可用 -Platforms 覆盖: -Platforms "linux-x64,win-x64"
# 支持平台: linux-x64 / linux-arm64(树莓派64位) / linux-arm(树莓派32位)
#           osx-x64(Intel Mac) / osx-arm64(Apple Silicon) / win-x64
# 产物: dist/（各平台部署包 + 源码包 + .sha256 校验）
# 需 Windows 10 1803+（自带 tar）；dotnet SDK + git 必需
# ============================================================
param(
    [string]$Version = "1.0.0",
    [string]$Platforms = "linux-x64,linux-arm64,linux-arm,osx-x64,osx-arm64,win-x64",
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
$Plats = $Platforms.Split(',') | Where-Object { $_ }

Write-Host "== 发布平台: $Platforms =="

# ---- 1. 逐平台发布 + 打包 ----
foreach ($RID in $Plats) {
    Write-Host "== [发布] $RID =="
    dotnet publish -c Release -r $RID --self-contained true `
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
      -o "publish/$RID" *>> $Log
    if ($LASTEXITCODE -ne 0) { Write-Host "[!] $RID 发布失败，查看 $Log"; exit 1 }

    $stage = Join-Path $tmp $RID
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    if ($RID -like "win-*") {
        # Windows: exe + README → zip
        Copy-Item "publish/$RID/emqx-monitor-server.exe" $stage/
        Copy-Item README.md $stage/
        Compress-Archive -Path (Join-Path $stage "*") -DestinationPath "$Dist/emqx-monitor-server-v$Version-$RID.zip" -Force
    }
    elseif ($RID -like "osx-*") {
        # macOS: 二进制 + README → tar.gz（mac 无 systemd，直接运行/launchd）
        Copy-Item "publish/$RID/emqx-monitor-server" $stage/
        Copy-Item README.md $stage/
        tar czf "$Dist/emqx-monitor-server-v$Version-$RID.tar.gz" -C $stage .
    }
    else {
        # Linux 系: 二进制 + deploy 脚本 + systemd 模板 + README → tar.gz
        Copy-Item "publish/$RID/emqx-monitor-server" $stage/
        Copy-Item deploy/emqx-monitor-server.service $stage/
        Copy-Item deploy-linux.sh $stage/
        Copy-Item README.md $stage/
        tar czf "$Dist/emqx-monitor-server-v$Version-$RID.tar.gz" -C $stage .
    }
    Write-Host "  ✓ $RID 打包完成"
}
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }

# ---- 2. 源码包 ----
Write-Host "== [源码包] git archive =="
git archive --format=tar.gz --prefix="emqx-monitor-server/" `
  -o "$Dist/emqx-monitor-server-v$Version-src.tar.gz" HEAD

# ---- 3. 校验和 ----
Write-Host "== [校验和] =="
Get-ChildItem "$Dist/emqx-monitor-server-v$Version-*" -File | Where-Object { $_.Extension -ne ".sha256" } | ForEach-Object {
    $hash = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLower()
    "$hash  $($_.Name)" | Out-File -FilePath "$($_.FullName).sha256" -Encoding ascii
}

Write-Host ""
Write-Host "== 完成 =="
Get-ChildItem $Dist | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "日志: $Log"

if ($Tag) {
    git tag "v$Version"
    Write-Host "git tag: v$Version 已创建（push 时用 git push --tags）"
}
