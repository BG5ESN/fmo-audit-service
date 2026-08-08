# ============================================================
# FMO Audit Service (FAS) — Windows 安装脚本 (PowerShell)
# 用法: irm https://bg5esn.com/share/fmo/fas-installer/install.ps1 | iex
# 安装: %LOCALAPPDATA%\FMOAuditService\fmo-audit-service.exe
# 服务: 可选注册 NSSM 服务 fmo-fas（需已安装 NSSM），否则手动运行/计划任务
# 配置: 安装后浏览器访问 http://<本机IP>:9527
# ============================================================
$ErrorActionPreference = "Stop"

$MetaUrl = "https://bg5esn.com/share/fmo/fas.json"
$InstallDir = Join-Path $env:LOCALAPPDATA "FMOAuditService"
$SvcName = "fmo-fas"
$Rid = "win-x64"

Write-Host "=== FMO Audit Service Installer (Windows) ===" -ForegroundColor Cyan

# STEP 1: 获取版本 + 下载地址（元数据 assets 兼容字符串 URL 与 {url:...} 对象两种格式）
Write-Host "[1/4] 获取最新版本..."
$meta = Invoke-RestMethod -Uri $MetaUrl -TimeoutSec 15
$version = $meta.version
$asset = $meta.assets.$Rid
$url = if ($asset -is [string]) { $asset } elseif ($asset -and $asset.url) { $asset.url } else { $null }
if (-not $url) {
    Write-Host "[!] 元数据中没有 win-x64 下载地址" -ForegroundColor Red
    exit 1
}
Write-Host "      版本: v$version"

$tmp = Join-Path $env:TEMP "fas_install"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

Write-Host "[2/4] 下载..."
$zip = Join-Path $tmp "fas.zip"
Invoke-WebRequest -Uri $url -OutFile $zip -TimeoutSec 120

# STEP 2: 停止旧实例（运行中的 exe 无法被覆盖；NSSM 服务 + 残留进程都要清）
$svc = Get-Service $SvcName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "[3/4] 停止旧服务 $SvcName ..."
    & nssm stop $SvcName 2>$null | Out-Null
    Start-Sleep 2
}
Get-Process fmo-audit-service -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1

# STEP 3: 解压 + 安装（产物是 zip 包，对齐 SAS）
Write-Host "[3/4] 安装到 $InstallDir ..."
Expand-Archive $zip -DestinationPath $tmp -Force
$exe = Get-ChildItem $tmp -Recurse -Filter "fmo-audit-service.exe" | Select-Object -First 1
if (-not $exe) {
    Write-Host "[!] 下载包中未找到 fmo-audit-service.exe" -ForegroundColor Red
    exit 1
}
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $exe.FullName (Join-Path $InstallDir "fmo-audit-service.exe") -Force

# STEP 4: 服务注册（NSSM 可选；数据目录显式指定，与提示一致）
Write-Host "[4/4] 服务注册..."
$nssm = Get-Command nssm -ErrorAction SilentlyContinue
if ($nssm) {
    & nssm install $SvcName (Join-Path $InstallDir "fmo-audit-service.exe") | Out-Null
    & nssm set $SvcName AppDirectory $InstallDir | Out-Null
    & nssm set $SvcName AppEnvironmentExtra "EMQX_MONITOR_DB=$InstallDir\fmo-audit-service.db" | Out-Null
    & nssm start $SvcName | Out-Null
    Write-Host "      NSSM 服务 $SvcName 已注册并启动" -ForegroundColor Green
} else {
    Write-Host "      未检测到 NSSM（https://nssm.cc），跳过服务注册" -ForegroundColor Yellow
    Write-Host "      手动运行: `"$(Join-Path $InstallDir 'fmo-audit-service.exe')`""
}

Remove-Item $tmp -Recurse -Force

Write-Host ""
Write-Host "=== FMO Audit Service v$version 安装完成 ===" -ForegroundColor Green
Write-Host "  访问: http://<本机IP>:9527"
Write-Host "  数据: $InstallDir\fmo-audit-service.db"
if ($nssm) {
    Write-Host "  升级: 页面「版本与更新」按钮（NSSM 下更新后服务不会自动重启，需执行: nssm restart $SvcName）"
    Write-Host "  卸载: nssm remove $SvcName confirm && Remove-Item `"$InstallDir`" -Recurse -Force（或使用官方 uninstall.ps1）"
} else {
    Write-Host "  升级: 页面「版本与更新」按钮（更新后请手动重启进程）"
}
