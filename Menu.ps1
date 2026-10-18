# ============================================================
#  商店应用修复工具 - 统一启动菜单
#  双击 启动菜单.bat 启动本菜单
# ============================================================

$ErrorActionPreference = 'Continue'

# ---- 提权 (UAC): 菜单提权后, 子工具不会再弹 UAC ----
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and $env:FIXAPPS_NOELEV -ne '1') {
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"')) -Verb RunAs
        exit
    } catch {
        Write-Host "[!] UAC 未确认, 以普通权限运行 (部分功能可能受限)" -ForegroundColor Yellow
    }
}

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$scriptDir = Split-Path -Parent $PSCommandPath

function Show-Menu {
    Clear-Host
    $w = 58
    Write-Host ""
    # 中日韩字符在控制台占 2 格, 按显示宽度居中才能对齐边框
    function Center([string]$s, $color) {
        $dw = 0
        foreach ($ch in $s.ToCharArray()) { if ([int]$ch -gt 255) { $dw += 2 } else { $dw += 1 } }
        $p = [Math]::Max(0, [int](($w - $dw) / 2))
        Write-Host ("║" + (" " * $p) + $s + (" " * ($w - $p - $dw)) + "║") -ForegroundColor $color
    }
    Write-Host ("╔" + ("═" * $w) + "╗") -ForegroundColor Cyan
    Write-Host ("║" + (" " * $w) + "║") -ForegroundColor Cyan
    Center "商店应用修复工具" 'White'
    Center "Store App Repair Kit" 'DarkCyan'
    Write-Host ("║" + (" " * $w) + "║") -ForegroundColor Cyan
    $os = (Get-CimInstance Win32_OperatingSystem).Caption
    Center ("系统: " + $os) 'DarkGray'
    Center ("权限: " + $(if ($isAdmin) { "管理员 [OK]" } else { "普通用户" })) 'DarkGray'
    Write-Host ("║" + (" " * $w) + "║") -ForegroundColor Cyan
    Write-Host ("╚" + ("═" * $w) + "╝") -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  请选择功能:" -ForegroundColor White
    Write-Host ""
    Write-Host "   [1] " -ForegroundColor Cyan -NoNewline; Write-Host "智能修复  " -NoNewline; Write-Host "自动找出坏掉的商店应用并修复 (推荐, 日常用这个)" -ForegroundColor Gray
    Write-Host "   [2] " -ForegroundColor Cyan -NoNewline; Write-Host "全量重置  " -NoNewline; Write-Host "重注册全部商店应用, 即经典的索引重置命令 (3-8 分钟)" -ForegroundColor Gray
    Write-Host "   [3] " -ForegroundColor Cyan -NoNewline; Write-Host "只看状态  " -NoNewline; Write-Host "扫描最近 72 小时故障记录, 不做任何修改" -ForegroundColor Gray
    Write-Host "   [4] " -ForegroundColor Cyan -NoNewline; Write-Host "打开日志  " -NoNewline; Write-Host "查看历史修复记录 (logs 目录)" -ForegroundColor Gray
    Write-Host "   [0] " -ForegroundColor Cyan -NoNewline; Write-Host "退出" -ForegroundColor Gray
    Write-Host ""
}

while ($true) {
    Show-Menu
    $choice = Read-Host "  请输入编号后回车"
    switch ($choice) {
        '1' {
            Write-Host ""
            Write-Host ">> 启动智能修复 (会短暂弹出被测试的应用窗口, 属正常现象) ..." -ForegroundColor Cyan
            & (Join-Path $scriptDir 'FixStoreApps.ps1') -Mode auto -NoPause
        }
        '2' {
            Write-Host ""
            Write-Host ">> 启动全量重置索引 ..." -ForegroundColor Cyan
            & (Join-Path $scriptDir 'ResetStoreIndex.ps1') -NoPause
        }
        '3' {
            Write-Host ""
            & (Join-Path $scriptDir 'FixStoreApps.ps1') -Mode status -NoPause
        }
        '4' {
            $logDir = Join-Path $scriptDir 'logs'
            if (-not (Test-Path $logDir) -or -not (Get-ChildItem $logDir -ErrorAction SilentlyContinue)) {
                Write-Host "  还没有任何日志" -ForegroundColor Yellow
            } else {
                Start-Process explorer.exe -ArgumentList $logDir
                Write-Host "  已打开日志目录" -ForegroundColor Green
            }
        }
        '0' { exit }
        default { continue }
    }
    if ($choice -in '1','2','3') {
        Write-Host ""
        Read-Host "  按回车返回菜单"
    }
}
