# ============================================================
# FMO Audit Service (FAS) 鈥?Windows 鍗歌浇鑴氭湰 (PowerShell)
# 鐢ㄦ硶: irm https://bg5esn.com/share/fmo/fas-installer/uninstall.ps1 | iex
# 璇存槑: 鍋滄骞跺垹闄?NSSM 鏈嶅姟 fmo-fas + 娈嬬暀杩涚▼ + 鍒犻櫎瀹夎鐩綍锛堝惈鍏ㄩ儴鏁版嵁锛夛紝
#       鎵ц鍗歌浇鍗崇敤鎴锋槑纭剰鎰匡紝鐩存帴褰诲簳鍒犻櫎锛堝榻?Linux uninstall.sh锛?
# ============================================================
$ErrorActionPreference = "Stop"

$InstallDir = Join-Path $env:LOCALAPPDATA "FMOAuditService"
$SvcName = "fmo-fas"

Write-Host "=== FMO Audit Service 鍗歌浇 ===" -ForegroundColor Cyan

# 鍋滄骞跺垹闄?NSSM 鏈嶅姟锛堣嫢瀛樺湪锛?
$svc = Get-Service $SvcName -ErrorAction SilentlyContinue
if ($svc) {
    & nssm stop $SvcName 2>$null | Out-Null
    Start-Sleep 2
    & nssm remove $SvcName confirm 2>$null | Out-Null
    Write-Host "宸茬Щ闄?NSSM 鏈嶅姟 $SvcName" -ForegroundColor Green
}

# 娓呯悊娈嬬暀杩涚▼
Get-Process fmo-audit-service -ErrorAction SilentlyContinue | Stop-Process -Force

# 鍒犻櫎瀹夎鐩綍锛堝惈鏁版嵁搴擄級
if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "宸插垹闄?$InstallDir" -ForegroundColor Green
}

Write-Host "鍗歌浇瀹屾垚" -ForegroundColor Green
