<#
.SYNOPSIS
  FMO Audit Service — OTA 元数据生成工具（PowerShell 版，对齐 gen-meta.sh）
.DESCRIPTION
  扫描 dist/ 下 fmo-audit-service-<rid>.tar.gz|.zip 产物（固定名，对齐 SAS），
  生成 fas.json —— URL 带版本目录，与 SAS 同风格。
#>
[CmdletBinding()]
param(
    [string]$Version = '',          # 版本号，默认取 git 最近 tag（发布版本 = tag，与 build-all 一致；无 tag 拒绝）
    [string]$BaseUrl = 'https://bg5esn.com/share/fmo/fmo-audit-service/',   # 产物/元数据基础 URL
    [string]$Notes = '',            # 发布说明（可选），写入元数据 notes 字段
    [switch]$Help                   # 显示帮助
)

# ── 简单帮助信息 ──
if ($Help) {
    Write-Host @"
FMO Audit Service - OTA 元数据生成工具 (PowerShell 版)

用法:
  powershell -File script/gen-meta.ps1 [-Version <版本>] [-BaseUrl <URL>] [-Notes <发布说明>] [-Help]

参数:
  -Version  版本号，默认取 git 最近 tag (如 2.0.8)，无 tag 则 0.0.0
  -BaseUrl  基础 URL，默认 https://bg5esn.com/share/fmo/fmo-audit-service/
  -Notes    发布说明（可选），写入元数据 notes 字段
  -Help     显示本帮助

示例:
  powershell -File script/gen-meta.ps1
  powershell -File script/gen-meta.ps1 -Version 2.0.12 -Notes "BUG FIX."

说明: 扫描 dist/ 下 fmo-audit-service-<rid>.tar.gz/.zip 产物，生成 fmo-audit-service.json；
      上传: 产物 -> <基础URL>/v<版本>/，元数据 -> <基础URL>/fas.json (覆盖)
"@
    exit 0
}

# ── 切到项目根（脚本位于 script/ 下）──
Set-Location (Split-Path -Parent $PSScriptRoot)

# ── 版本：显式参数优先，否则取 git 最近 tag（--abbrev=0 只取 tag 名，不带脏后缀）──
if (-not $Version) {
    $tag = & git describe --tags --abbrev=0 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $tag) {
        Write-Host "[!] 未找到 git tag，请先打 tag（发布版本必须可追溯，与 build-all 一致）: git tag v<版本>" -ForegroundColor Red
        exit 1
    }
    $Version = $tag -replace '^v', ''
}
$BaseUrl = $BaseUrl.TrimEnd('/') + '/'   # 保证尾斜杠，URL 拼接不出双斜杠

$DIST = 'dist'
$OUT  = 'fas.json'

Write-Host "== FMO Audit Service 元数据生成 v$Version =="
Write-Host "基础 URL: $BaseUrl"
if ($Notes) { Write-Host "发布说明: $Notes" }

# ── 扫描产物：fmo-audit-service-<rid>.tar.gz / .zip（固定名，对齐 SAS）──
$RIDS = @('linux-x64', 'linux-arm64', 'linux-arm', 'osx-x64', 'osx-arm64', 'win-x64')
$assets = @{}
$count = 0
foreach ($rid in $RIDS) {
    foreach ($ext in @('.tar.gz', '.zip')) {
        $f = Join-Path $DIST "fmo-audit-service-$rid$ext"
        if (Test-Path $f) {
            $name = Split-Path $f -Leaf
            # URL 带版本目录：<base>/v<版本>/<固定名>（对齐 SAS sas/v1.0.5/xxx）
            $assets[$rid] = "${BaseUrl}v${Version}/$name"
            $count++
            Write-Host "  + $name -> rid=$rid (v${Version}/)"
        }
    }
}

if ($count -eq 0) {
    Write-Host "[!] dist/ 下没有找到产物（fmo-audit-service-<rid>.tar.gz/.zip），请先运行 build-all.sh" -ForegroundColor Red
    exit 1
}

# ── 生成元数据（UTF-8 无 BOM：PowerShell 5.1 Out-File 默认 UTF-16，必须显式）──
$meta = [ordered]@{ version = $Version }
if ($Notes) { $meta.notes = $Notes }
$meta.url = 'https://github.com/BG5ESN/fmo-audit-service'
$meta.assets = $assets

$json = $meta | ConvertTo-Json -Depth 3
[System.IO.File]::WriteAllText((Join-Path (Get-Location) $OUT), $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "== 已生成 $OUT =="
Get-Content $OUT
Write-Host ""
Write-Host "上传步骤:"
Write-Host "  1. 产物 → ${BaseUrl}v${Version}/"
Write-Host "  2. 元数据 → ${BaseUrl}fas.json（覆盖）"
