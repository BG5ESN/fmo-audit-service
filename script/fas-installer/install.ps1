# ============================================================
# FMO Audit Service (FAS) — Windows 安装脚本 (PowerShell)
# 用法: irm https://bg5esn.com/share/fmo/fas-installer/install.ps1 | iex
# 安装: %LOCALAPPDATA%\FMOAuditService\fmo-audit-service.exe
# 配置: 安装后浏览器访问 http://<本机IP>:9527
# 服务: 可选注册 NSSM 服务（需已安装 NSSM），否则手动运行/计划任务
# ============================================================
$ErrorActionPreference = "Stop"

$MetaUrl = "https://bg5esn.com/share/fmo/fmo-audit-service.json"
$InstallDir = Join-Path $env:LOCALAPPDATA "FMOAuditService"
$Rid = "win-x64"

Write-Host "=== FMO Audit Service Installer (Windows) ===" -ForegroundColor Cyan

# STEP 1: 获取版本 + 下载
Write-Host "[1/4] 获取最新版本..."
$meta = Invoke-RestMethod -Uri $MetaUrl -TimeoutSec 15
$version = $meta.version
$asset = $meta.assets.$Rid
if (-not $asset) {
    Write-Host "[!] 元数据中没有 win-x64 下载地址" -ForegroundColor Red
    exit 1
}
Write-Host "      版本: v$version"

$tmp = Join-Path $env:TEMP "fas_install"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

Write-Host "[2/4] 下载..."
$zip = Join-Path $tmp "fas.zip"
Invoke-WebRequest -Uri $asset.url -OutFile $zip -TimeoutSec 120

# sha256 校验
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
if ($asset.sha256 -and $actual -ne $asset.sha256.ToLower()) {
    Write-Host "[!] sha256 校验失败：文件可能被篡改" -ForegroundColor Red
    exit 1
}
Write-Host "      sha256 校验通过"

# STEP 2: 解压 + 安装
Write-Host "[3/4] 安装到 $InstallDir ..."
Expand-Archive $zip -DestinationPath $tmp -Force
$exe = Get-ChildItem $tmp -Recurse -Filter "fmo-audit-service.exe" | Select-Object -First 1
if (-not $exe) {
    Write-Host "[!] 下载包中未找到 fmo-audit-service.exe" -ForegroundColor Red
    exit 1
}
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $exe.FullName (Join-Path $InstallDir "fmo-audit-service.exe") -Force

# STEP 3: 服务注册（NSSM 可选）
Write-Host "[4/4] 服务注册..."
$nssm = Get-Command nssm -ErrorAction SilentlyContinue
if ($nssm) {
    & nssm install fmo-audit-service (Join-Path $InstallDir "fmo-audit-service.exe") | Out-Null
    & nssm set fmo-audit-service AppDirectory $InstallDir | Out-Null
    & nssm start fmo-audit-service | Out-Null
    Write-Host "      NSSM 服务 fmo-audit-service 已注册并启动" -ForegroundColor Green
} else {
    Write-Host "      未检测到 NSSM（https://nssm.cc），跳过服务注册" -ForegroundColor Yellow
    Write-Host "      手动运行: `"$(Join-Path $InstallDir 'fmo-audit-service.exe')`""
}

Remove-Item $tmp -Recurse -Force

Write-Host ""
Write-Host "=== FMO Audit Service v$version 安装完成 ===" -ForegroundColor Green
Write-Host "  访问: http://<本机IP>:9527"
Write-Host "  升级: 页面「版本与更新」按钮"
Write-Host "  数据: $InstallDir\fmo-audit-service.db"
