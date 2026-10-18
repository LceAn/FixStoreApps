param(
    [int]$Max = 0,
    [switch]$NoPause
)

# ============================================================
#  重置商店应用索引 (用户原始命令的可视化版本)
#  原命令:
#    Get-AppxPackage | ForEach-Object { Add-AppxPackage -DisableDevelopmentMode -Register "$($_.InstallLocation)\AppXManifest.xml" }
#  作用: 把当前用户的所有商店(UWP/MSIX)应用从磁盘清单重新注册一遍
#  耗时: 约 3-8 分钟
# ============================================================

$ErrorActionPreference = 'Continue'

# ---- 提权 (UAC) ----
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and $env:FIXAPPS_NOELEV -ne '1') {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
    if ($Max -gt 0) { $argList += @('-Max', $Max) }
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs
        exit
    } catch {
        Write-Host ""
        Write-Host "[!] UAC 未确认, 以普通权限继续" -ForegroundColor Yellow
    }
}

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$scriptDir = Split-Path -Parent $PSCommandPath
$logDir = Join-Path $scriptDir 'logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$logFile = Join-Path $logDir ("reset-index-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")

function Log($msg) {
    $line = $msg
    $c = 'Gray'
    if     ($msg -match '^==== 完成')        { $c = 'Green' }
    elseif ($msg -match '^====')              { $c = 'DarkCyan' }
    elseif ($msg -match '失败|- ')            { $c = 'DarkYellow' }
    elseif ($msg -match '『')                 { $c = 'Cyan' }
    Write-Host $line -ForegroundColor $c
    Add-Content -Path $logFile -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
}

# 计算控制台显示宽度 (中日韩字符占 2 格)
function Get-DisplayWidth([string]$s) {
    $w = 0
    foreach ($ch in $s.ToCharArray()) { if ([int]$ch -gt 255) { $w += 2 } else { $w += 1 } }
    return $w
}

function Write-Banner($title, $subtitle) {
    $w = 58
    $tw = Get-DisplayWidth $title
    $pad = [Math]::Max(0, [int](($w - $tw) / 2))
    Write-Host ""
    Write-Host ("╔" + ("═" * $w) + "╗") -ForegroundColor Cyan
    Write-Host ("║" + (" " * $w) + "║") -ForegroundColor Cyan
    Write-Host ("║" + (" " * $pad) + $title + (" " * ($w - $pad - $tw)) + "║") -ForegroundColor Cyan
    foreach ($s in @($subtitle)) {
        if ($s) {
            $sw = Get-DisplayWidth $s
            $sp = [Math]::Max(0, [int](($w - $sw) / 2))
            Write-Host ("║" + (" " * $sp) + $s + (" " * ($w - $sp - $sw)) + "║") -ForegroundColor DarkCyan
        }
    }
    Write-Host ("║" + (" " * $w) + "║") -ForegroundColor Cyan
    Write-Host ("╚" + ("═" * $w) + "╝") -ForegroundColor Cyan
    Write-Host ""
}

Write-Banner "重置商店应用索引" ("全量重注册当前用户的商店应用 · " + (Get-Date -Format 'yyyy-MM-dd HH:mm'))
Log ("日志: " + $logFile)
Log "提示: 建议先关闭正在使用的应用再继续 (正在运行的软件重注册可能失败)"

$pkgs = Get-AppxPackage
if ($Max -gt 0) { $pkgs = $pkgs | Select-Object -First $Max }
$total = @($pkgs).Count
Log ("共 " + $total + " 个应用, 开始重注册 ...")
Log ""

$ok = 0; $fail = 0; $failedNames = @(); $i = 0
foreach ($p in $pkgs) {
    $i++
    Write-Progress -Activity "正在重注册商店应用" -Status ("$i / $total  " + $p.Name) -PercentComplete (($i / $total) * 100)
    try {
        Add-AppxPackage -DisableDevelopmentMode -Register ("$($p.InstallLocation)\AppXManifest.xml") -ErrorAction Stop
        $ok++
    } catch {
        $fail++
        $failedNames += $p.Name
    }
}
Write-Progress -Activity "正在重注册商店应用" -Completed

Log ""
Log ("==== 完成: 成功 " + $ok + " 个, 失败 " + $fail + " 个 ====")
if ($fail -gt 0) {
    Log "失败的包 (绝大多数是框架包/系统组件, 属正常现象, 不用管):"
    $failedNames | Select-Object -Unique -First 15 | ForEach-Object { Log ("   - " + $_) }
}
Log ""
Log "如果某个应用还是打不开 -> 用『一键修复商店应用』, 它会自动找出坏的应用并修复+验证"
Log ""
if (-not $NoPause) { Read-Host "按回车关闭窗口" }
