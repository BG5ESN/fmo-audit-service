# ============================================================
# FMO Audit Service (FAS) — Windows 安装脚本 (PowerShell)
# 用法: irm https://bg5esn.com/share/fmo/fas-installer/install.ps1 -OutFile "$env:TEMP\fas-install.ps1"; iex (Get-Content "$env:TEMP\fas-install.ps1" -Raw -Encoding UTF8)
# 安装: %LOCALAPPDATA%\FMOAuditService\fmo-audit-service.exe
# 服务: Windows 计划任务 fmo-fas（内置，零依赖）
# 配置: 安装后浏览器访问 http://<本机IP>:9527 首次设置管理员并连接 EMQX
# ============================================================
$ErrorActionPreference = "Stop"

$MetaUrl = "https://bg5esn.com/share/fmo/fas.json"
$InstallDir = Join-Path $env:LOCALAPPDATA "FMOAuditService"
$SvcName = "fmo-fas"
$Rid = "win-x64"

# ── 管理员检查 ──
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[!] 请以管理员身份运行 PowerShell" -ForegroundColor Red
    Write-Host "    右键 PowerShell -> 以管理员身份运行，然后执行:"
    Write-Host '    irm https://bg5esn.com/share/fmo/fas-installer/install.ps1 -OutFile "$env:TEMP\fas-install.ps1"; iex (Get-Content "$env:TEMP\fas-install.ps1" -Raw -Encoding UTF8)'
    exit 1
}

Write-Host ""
Write-Host "=== FMO Audit Service Installer (Windows) ===" -ForegroundColor Cyan
Write-Host ""

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

# STEP 2: 停止旧实例（计划任务 + 残留进程）
$existingTask = Get-ScheduledTask -TaskName $SvcName -ErrorAction SilentlyContinue
if ($existingTask) {
    Write-Host "[3/4] 停止旧任务 $SvcName ..."
    Stop-ScheduledTask -TaskName $SvcName -ErrorAction SilentlyContinue
    Get-Process fmo-audit-service -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 2
    Write-Host "      已停止旧版 $SvcName 任务"
}

# STEP 3: 解压 + 安装（产物是 zip 包，对齐 SAS）
Write-Host "[3/4] 安装到 $InstallDir ..."
if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
Expand-Archive $zip -DestinationPath $InstallDir -Force
Remove-Item $zip -Force

$exePath = Join-Path $InstallDir "fmo-audit-service.exe"
if (-not (Test-Path $exePath)) {
    Write-Host "[!] 下载包异常，未找到 fmo-audit-service.exe 主程序" -ForegroundColor Red
    exit 1
}
Remove-Item $tmp -Recurse -Force

# STEP 4: 注册计划任务（Windows 内置，零依赖；对齐 SAS）
Write-Host "[4/4] 注册系统任务..."

$logPath = Join-Path $InstallDir "fas.log"
$dbPath = Join-Path $InstallDir "fmo-audit-service.db"

# 注册新任务：开机自启 + 崩溃自动重启（间隔 1 分钟，最多 3 次）
# 日志重定向到 fas.log，注意保留历史日志（> 改为 >>）
$cmd = 'set "EMQX_MONITOR_DB=' + $dbPath + '" && "' + $exePath + '" >> "' + $logPath + '" 2>&1'

$action = New-ScheduledTaskAction `
    -Execute "cmd.exe" `
    -Argument ('/c "' + $cmd + '"') `
    -WorkingDirectory $InstallDir

$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -RestartCount 3 `
    -ExecutionTimeLimit (New-TimeSpan -Days 9999)

$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest

# 移除旧任务（若存在）
if ($existingTask) {
    Unregister-ScheduledTask -TaskName $SvcName -Confirm:$false -ErrorAction SilentlyContinue
}

Register-ScheduledTask `
    -TaskName $SvcName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description "FMO Audit Service" | Out-Null

Start-ScheduledTask -TaskName $SvcName
Write-Host "      等待服务启动 (5 秒)..."
Start-Sleep -Seconds 5
Write-Host ""
Write-Host "  ── 启动日志 ──"
if (Test-Path $logPath) {
    Get-Content $logPath | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Host "  (日志文件尚未生成)"
}
Write-Host "  ──────────────"

Write-Host ""
Write-Host "=== FMO Audit Service v$version 安装完成并已启动 ===" -ForegroundColor Green
Write-Host ""
Write-Host "  访问:     http://<本机IP>:9527"
Write-Host "  首次使用: 设置管理员账号 -> 配置页填入 EMQX 地址 + API 密钥 -> 启用主题统计"
Write-Host ""
Write-Host "  日志:     Get-Content -Wait '$logPath'"
Write-Host ""
Write-Host "  状态:     Get-ScheduledTask -TaskName $SvcName"
Write-Host "  启动:     Start-ScheduledTask -TaskName $SvcName"
Write-Host "  停止:     Stop-ScheduledTask -TaskName $SvcName"
Write-Host "  重启:     Stop-ScheduledTask $SvcName; Start-ScheduledTask $SvcName"
Write-Host "  升级:     页面「版本与更新」按钮（OTA 更新后任务不会自动重启，需手动 Start-ScheduledTask $SvcName）；或 Stop-ScheduledTask $SvcName; & '$exePath' --update; Start-ScheduledTask $SvcName"
Write-Host "  卸载:     irm https://bg5esn.com/share/fmo/fas-installer/uninstall.ps1 -OutFile `"$env:TEMP\fas-uninstall.ps1`"; iex (Get-Content `"$env:TEMP\fas-uninstall.ps1`" -Raw -Encoding UTF8)"
Write-Host ""
Write-Host "  安装目录: $InstallDir"
