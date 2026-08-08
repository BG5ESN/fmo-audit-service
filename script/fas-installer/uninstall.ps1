# ============================================================
# FMO Audit Service (FAS) — Windows 卸载脚本 (PowerShell)
# 用法: irm https://bg5esn.com/share/fmo/fas-installer/uninstall.ps1 -OutFile "$env:TEMP\fas-uninstall.ps1"; iex (Get-Content "$env:TEMP\fas-uninstall.ps1" -Raw -Encoding UTF8)
# 说明: 停止并删除计划任务 fmo-fas + 残留进程 + 删除安装目录（含全部数据），
#       执行卸载即用户明确意愿，直接彻底删除（对齐 Linux uninstall.sh）
# ============================================================
$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "=== FMO Audit Service 卸载 ===" -ForegroundColor Cyan
Write-Host ""

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[!] 请以管理员身份运行 PowerShell" -ForegroundColor Red
    Write-Host "    右键 PowerShell -> 以管理员身份运行，然后执行:"
    Write-Host '    irm https://bg5esn.com/share/fmo/fas-installer/uninstall.ps1 -OutFile "$env:TEMP\fas-uninstall.ps1"; iex (Get-Content "$env:TEMP\fas-uninstall.ps1" -Raw -Encoding UTF8)'
    exit 1
}

$SvcName = "fmo-fas"
$InstallDir = Join-Path $env:LOCALAPPDATA "FMOAuditService"

# 停止并删除计划任务
Write-Host "[1/2] 停止并移除计划任务..."
$task = Get-ScheduledTask -TaskName $SvcName -ErrorAction SilentlyContinue
if ($task) {
    Stop-ScheduledTask -TaskName $SvcName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $SvcName -Confirm:$false
    Write-Host "      完成" -ForegroundColor Green
} else {
    Write-Host "      未找到计划任务 (已跳过)" -ForegroundColor Green
}

# 清理残留进程
Get-Process fmo-audit-service -ErrorAction SilentlyContinue | Stop-Process -Force

# 删除安装目录（含全部数据）
Write-Host "[2/2] 删除程序和配置..."
if (Test-Path $InstallDir) {
    Remove-Item -Recurse -Force $InstallDir
    Write-Host "      已删除: $InstallDir" -ForegroundColor Green
} else {
    Write-Host "      目录不存在 (已跳过)" -ForegroundColor Green
}

Write-Host ""
Write-Host "=== FMO Audit Service 已完全卸载 ===" -ForegroundColor Green
Write-Host ""
