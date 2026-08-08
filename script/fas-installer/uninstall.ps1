# ============================================================
# FMO Audit Service (FAS) — Windows 卸载脚本 (PowerShell)
# 用法: irm https://bg5esn.com/share/fmo/fas-installer/uninstall.ps1 | iex
# 说明: 停止并删除 NSSM 服务 fmo-fas + 残留进程 + 删除安装目录（含全部数据），
#       执行卸载即用户明确意愿，直接彻底删除（对齐 Linux uninstall.sh）
# ============================================================
$ErrorActionPreference = "Stop"

$InstallDir = Join-Path $env:LOCALAPPDATA "FMOAuditService"
$SvcName = "fmo-fas"

Write-Host "=== FMO Audit Service 卸载 ===" -ForegroundColor Cyan

# 停止并删除 NSSM 服务（若存在）
$svc = Get-Service $SvcName -ErrorAction SilentlyContinue
if ($svc) {
    & nssm stop $SvcName 2>$null | Out-Null
    Start-Sleep 2
    & nssm remove $SvcName confirm 2>$null | Out-Null
    Write-Host "已移除 NSSM 服务 $SvcName" -ForegroundColor Green
}

# 清理残留进程
Get-Process fmo-audit-service -ErrorAction SilentlyContinue | Stop-Process -Force

# 删除安装目录（含数据库）
if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "已删除 $InstallDir" -ForegroundColor Green
}

Write-Host "卸载完成" -ForegroundColor Green
