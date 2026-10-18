param(
    [string]$Mode = 'auto',
    [string]$Name = '',
    [switch]$NoPause
)

# ============================================================
#  一键修复商店应用 (One-click Store/UWP app repair)
#  用法:
#    双击 一键修复商店应用.bat          -> 自动扫描 + 修复
#    FixStoreApps.ps1 -Mode status      -> 只查看, 不修复
#    FixStoreApps.ps1 -Name Teams       -> 修复指定应用(关键字)
#  说明: 需要 UAC 点一次"是"; 全程只动出问题的应用, 不碰系统其他部分
# ============================================================

$ErrorActionPreference = 'Continue'

# ---- 提权 (UAC) : 环境变量 FIXAPPS_NOELEV=1 可跳过(调试用) ----
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and $env:FIXAPPS_NOELEV -ne '1') {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
    if ($Mode -ne 'auto') { $argList += @('-Mode', $Mode) }
    if ($Name) { $argList += @('-Name', $Name) }
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs
        exit   # 提权实例已接手
    } catch {
        Write-Host ""
        Write-Host "[!] UAC 未确认, 以普通权限继续 (绝大多数修复仍然可用)" -ForegroundColor Yellow
    }
}

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ---- 日志 ----
$scriptDir = Split-Path -Parent $PSCommandPath
$logDir = Join-Path $scriptDir 'logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$logFile = Join-Path $logDir ("repair-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")

# 彩色输出: 文件里记纯文本, 控制台按内容自动着色
function Log($msg) {
    $line = "[" + (Get-Date -Format 'HH:mm:ss') + "] " + $msg
    $c = 'Gray'
    if     ($msg -match '\[ok\]|>>|已修复|重装成功') { $c = 'Green' }
    elseif ($msg -match '仍坏')                       { $c = 'Red' }
    elseif ($msg -match '\[!\]')                       { $c = 'Yellow' }
    elseif ($msg -match '\[x\]')                       { $c = 'Red' }
    elseif ($msg -match '^====')                       { $c = 'DarkCyan' }
    elseif ($msg -match '^--')                         { $c = 'White' }
    elseif ($msg -match '启动测试|检查:|扫描')         { $c = 'Cyan' }
    Write-Host $line -ForegroundColor $c
    Add-Content -Path $logFile -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
}

# 计算控制台显示宽度 (中日韩字符占 2 格)
function Get-DisplayWidth([string]$s) {
    $w = 0
    foreach ($ch in $s.ToCharArray()) { if ([int]$ch -gt 255) { $w += 2 } else { $w += 1 } }
    return $w
}

# 居中的双线标题框
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

# 已知应用的 winget 重装映射
$script:WingetMap = @{
    'OpenAI.Codex'              = @{ id = '9PLM9XGG6VKS';        source = 'msstore' }
    'PerplexityAI.PerplexityApp' = @{ id = '9P9XG917PWCJ';       source = 'msstore' }
    'Microsoft.WindowsTerminal' = @{ id = '9N0DX20HK70B';        source = 'msstore' }
    'Microsoft.ScreenSketch'    = @{ id = '9N95G1CHX4WZ';        source = 'msstore' }
    'Agilebits.1Password'       = @{ id = 'AgileBits.1Password'; source = 'winget' }
    'Microsoft.WindowsStore'    = @{ id = '9WZDNCRFJBMP';        source = 'msstore' }
}

# ---------------- 基础函数 ----------------

function Get-RegPkg($family) {
    return Get-AppxPackage | Where-Object { $_.PackageFamilyName -eq $family } | Select-Object -First 1
}

function Get-Aumid($pkg) {
    try {
        $man = Get-AppxPackageManifest $pkg
        $app = $man.Package.Applications.Application | Select-Object -First 1
        if ($app) { return $pkg.PackageFamilyName + '!' + $app.Id }
    } catch { }
    return $null
}

# 真实激活测试: 启动 -> 等待 -> 看新进程 + 新错误
function Test-Activation($pkg, [int]$waitSec = 20, [bool]$keepRunning = $false) {
    $res = @{ ok = $false; procs = 0; errs = 0; note = '' }
    $aumid = Get-Aumid $pkg
    if (-not $aumid) { $res.note = '无法读取应用清单'; $res.errs = -1; return $res }

    # 从清单拿可执行文件名, 用于补充进程匹配 (有的应用进程 Path 读不到)
    $exeNames = @()
    try {
        $man = Get-AppxPackageManifest $pkg
        $exeNames = @($man.Package.Applications.Application | ForEach-Object {
            if ($_.Executable) { [IO.Path]::GetFileNameWithoutExtension($_.Executable) }
        })
    } catch { }

    $t0 = Get-Date
    Start-Process explorer.exe -ArgumentList ("shell:AppsFolder\" + $aumid)
    Start-Sleep -Seconds $waitSec

    $newProcs = @(Get-Process | Where-Object {
        $m = $false
        try { if ($_.Path -and $_.Path -like ($pkg.InstallLocation + '\*') -and $_.StartTime -gt $t0.AddSeconds(-3)) { $m = $true } } catch { }
        if (-not $m -and ($exeNames -contains $_.Name)) {
            try { if ($_.StartTime -gt $t0.AddSeconds(-3)) { $m = $true } } catch { }
        }
        $m
    })
    $oldProcs = @(Get-Process | Where-Object {
        $m = $false
        try { if ($_.Path -and $_.Path -like ($pkg.InstallLocation + '\*')) { $m = $true } } catch { }
        if (-not $m -and ($exeNames -contains $_.Name)) { $m = $true }
        $m
    })

    $newErrs = @(Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-AppModel-Runtime/Admin'; Level=2; StartTime=$t0.AddSeconds(-3)} -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match [regex]::Escape($pkg.PackageFullName) -or $_.Message -match [regex]::Escape($pkg.PackageFamilyName) })

    $res.procs = $newProcs.Count
    $res.errs = $newErrs.Count
    if ($newProcs.Count -gt 0 -and $newErrs.Count -eq 0) { $res.ok = $true }
    elseif ($newErrs.Count -eq 0 -and $oldProcs.Count -gt 0) { $res.ok = $true; $res.note = '已在运行' }
    if (-not $keepRunning) {
        foreach ($p in $newProcs) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
    return $res
}

# 修复杂 ladder: 重注册 -> 数据目录重建+重注册 -> Reset
function Repair-App($family) {
    $pkg = Get-RegPkg $family
    if (-not $pkg) { Log "    [x] 系统里找不到该包 ($family)"; return 'missing' }
    Log ("    当前版本: v" + $pkg.Version)

    # ---- 1. 重注册 ----
    Log "  [1/3] 从清单重注册 ..."
    Add-AppxPackage -DisableDevelopmentMode -Register ("$($pkg.InstallLocation)\AppXManifest.xml") -ErrorAction SilentlyContinue
    $r = Test-Activation $pkg
    Log ("        启动测试: 进程=" + $r.procs + " 新错误=" + $r.errs)
    if ($r.ok) { Log "        >> 已修复 (重注册)"; return 'fixed' }

    # ---- 2. 杀进程 + 重建数据目录 + 重注册 ----
    Log "  [2/3] 重建应用数据目录 + 重注册 ..."
    Get-Process | Where-Object { try { $_.Path -like ($pkg.InstallLocation + '\*') } catch { $false } } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $dataDir = Join-Path $env:LOCALAPPDATA ("Packages\" + $pkg.PackageFamilyName)
    if (Test-Path $dataDir) {
        $bakName = $pkg.PackageFamilyName + ".bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
        try {
            Rename-Item -Path $dataDir -NewName $bakName -Force -ErrorAction Stop
            Log ("        旧数据目录已备份为: " + $bakName)
        } catch {
            Log ("        [!] 数据目录改名失败: " + $_.Exception.Message)
        }
    }
    Add-AppxPackage -DisableDevelopmentMode -Register ("$($pkg.InstallLocation)\AppXManifest.xml") -ErrorAction SilentlyContinue
    $r = Test-Activation $pkg
    Log ("        启动测试: 进程=" + $r.procs + " 新错误=" + $r.errs)
    if ($r.ok) { Log "        >> 已修复 (数据目录重建)"; return 'fixed' }

    # ---- 3. Reset (等效 设置->应用->重置) ----
    if (Get-Command Reset-AppxPackage -ErrorAction SilentlyContinue) {
        Log "  [3/3] Reset-AppxPackage (重置应用) ..."
        Reset-AppxPackage -Package $pkg -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
        $r = Test-Activation $pkg
        Log ("        启动测试: 进程=" + $r.procs + " 新错误=" + $r.errs)
        if ($r.ok) { Log "        >> 已修复 (重置)"; return 'fixed' }
    } else {
        Log "  [3/3] 此系统没有 Reset-AppxPackage, 跳过"
    }

    Log "    [x] 自动修复三步都无效, 需要卸载重装"
    return 'need-reinstall'
}

# 卸载 + winget 重装 (需要确认)
function Reinstall-App($family) {
    $matchKey = $script:WingetMap.Keys | Where-Object { $family -like ("*" + $_ + "*") } | Select-Object -First 1
    if (-not $matchKey) {
        Log "    [x] 没有这个应用的重装信息, 请手动到微软商店搜索安装"
        return 'failed'
    }
    $info = $script:WingetMap[$matchKey]
    $ans = Read-Host ("    现在自动卸载并重装 " + $family + " ? (y=是 / 其他=否)")
    if ($ans -ne 'y') { Log "    跳过重装"; return 'failed' }

    Log "    卸载 ..."
    Get-AppxPackage | Where-Object { $_.PackageFamilyName -eq $family } | Remove-AppxPackage -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3

    $winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
    if (-not (Test-Path $winget)) { $winget = 'winget.exe' }
    $srcArg = @()
    if ($info.source -eq 'msstore') { $srcArg = @('--source', 'msstore') } else { $srcArg = @('--source', 'winget') }
    $allArgs = @('install', '--id', $info.id) + $srcArg + @('--accept-package-agreements', '--accept-source-agreements')

    for ($attempt = 1; $attempt -le 2; $attempt++) {
        Log ("    winget 安装 (第 " + $attempt + " 次尝试) ...")
        $proc = Start-Process -FilePath $winget -ArgumentList $allArgs -PassThru -WindowStyle Hidden
        if (-not $proc.WaitForExit(480000)) {
            Log "    [!] winget 卡住 (8 分钟无进展), 强制结束并重启安装服务"
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            Restart-Service AppXSvc -Force -ErrorAction SilentlyContinue
            Restart-Service InstallService -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 5
        } else {
            break
        }
    }

    $pkg = Get-RegPkg $family
    if ($pkg) {
        $r = Test-Activation $pkg
        Log ("    安装后启动测试: 进程=" + $r.procs + " 新错误=" + $r.errs)
        if ($r.ok) { Log "    >> 重装成功"; return 'fixed' }
    } else {
        Log "    [x] 重装后系统里没有这个包"
    }
    Log "    [x] 自动重装失败, 请手动到微软商店安装"
    return 'failed'
}

# ---------------- 扫描 ----------------

function Get-BrokenCandidates {
    # 72 小时内的激活失败事件 -> 包列表
    $since = (Get-Date).AddHours(-72)
    $events = @(Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-AppModel-Runtime/Admin'; Level=2; StartTime=$since} -ErrorAction SilentlyContinue)
    $map = @{}
    foreach ($e in $events) {
        if ($e.Message -match '([A-Za-z0-9.\-]+_\d+(?:\.\d+)*_(?:x64|arm64|x86|neutral)__[A-Za-z0-9]+)') {
            $fn = $Matches[1]
            if (-not $map[$fn]) { $map[$fn] = @{ count = 0; last = $e.TimeCreated } }
            $map[$fn].count++
            if ($e.TimeCreated -gt $map[$fn].last) { $map[$fn].last = $e.TimeCreated }
        }
    }
    # TWinUI 5973 (点击图标时的激活失败, 消息里是包族名)
    $tw = @(Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-TWinUI/Operational'; Id=5973; StartTime=$since} -ErrorAction SilentlyContinue)
    foreach ($e in $tw) {
        if ($e.Message -match '([A-Za-z0-9.]+_[A-Za-z0-9]{13})') {
            $fam = $Matches[1]
            if (-not $map[$fam]) { $map[$fam] = @{ count = 0; last = $e.TimeCreated } }
            $map[$fam].count++
        }
    }

    # 归并到包族: 包全名(Name_版本_架构__哈希) -> 包族(Name__哈希), 同族合并计数
    $result = @{}
    foreach ($key in $map.Keys) {
        $family = $key
        if ($key -match '^(?<name>[A-Za-z0-9.\-]+)_(?<ver>\d+(?:\.\d+)*)_(?<arch>x64|arm64|x86|neutral)__(?<hash>[A-Za-z0-9]+)$') {
            $family = $Matches['name'] + '_' + $Matches['hash']
        }
        $pkg = Get-RegPkg $family
        $isFramework = $false; $status = '未安装'; $version = ''
        if ($pkg) {
            $family = $pkg.PackageFamilyName
            $isFramework = [bool]$pkg.IsFramework
            $status = $pkg.Status
            $version = $pkg.Version
        }
        if ($result[$family]) {
            $result[$family].count += $map[$key].count
            if ($map[$key].last -gt $result[$family].last) { $result[$family].last = $map[$key].last }
        } else {
            $result[$family] = @{
                FullName = $key; Family = $family; count = $map[$key].count
                last = $map[$key].last; Installed = [bool]$pkg; Framework = $isFramework
                Status = $status; Version = $version
            }
        }
    }
    return ($result.Values | Sort-Object { $_.count } -Descending)
}

function Find-AppsByName($keyword) {
    # 按关键字找包: 包名 / 包族名 / 开始菜单显示名
    $kwl = $keyword.ToLower()
    $hits = @()
    foreach ($p in (Get-AppxPackage | Where-Object { -not $_.IsFramework })) {
        if ($p.Name.ToLower().Contains($kwl) -or $p.PackageFamilyName.ToLower().Contains($kwl)) { $hits += $p }
    }
    if ($hits.Count -eq 0) {
        foreach ($s in (Get-StartApps)) {
            if ($s.Name.ToLower().Contains($kwl) -and $s.AppID -like '*!*') {
                $fam = ($s.AppID -split '!')[0]
                $p = Get-RegPkg $fam
                if ($p) { $hits += $p }
            }
        }
    }
    $seen = @{}; $out = @()
    foreach ($p in $hits) { if (-not $seen[$p.PackageFamilyName]) { $seen[$p.PackageFamilyName] = $true; $out += $p } }
    return $out
}

# ---------------- 环境检查 ----------------

function Environment-Check {
    Log ""
    Log "==== 环境检查 ===="
    # 存储感知 (曾被发现会清掉应用数据)
    $ss = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy' -ErrorAction SilentlyContinue
    if ($ss -and $ss.'01' -gt 0) {
        Log "  [!] 存储感知(自动清理)开着 —— 它曾疑似清坏应用数据"
        $ans = Read-Host "      现在帮你关掉吗? (y=是 / 其他=保留)"
        if ($ans -eq 'y') {
            Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy' -Name '01' -Value 0 -Type DWord
            Log "      存储感知已关闭"
        }
    } else {
        Log "  [ok] 存储感知(自动清理)已关闭"
    }
    # 快速启动
    $fs = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -ErrorAction SilentlyContinue
    if ($fs -and $fs.HiberbootEnabled -gt 0) {
        Log "  [!] 快速启动开着 —— 关机不留干净状态, 建议关闭"
        $ans = Read-Host "      现在帮你关掉吗? (y=是 / 其他=保留)"
        if ($ans -eq 'y') {
            Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name 'HiberbootEnabled' -Value 0 -Type DWord
            Log "      快速启动已关闭 (下次关机生效)"
        }
    } else {
        Log "  [ok] 快速启动已关闭"
    }
    # 商店自动更新策略
    $pol = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore' -ErrorAction SilentlyContinue
    if ($pol -and $pol.AutoDownload -eq 2) {
        Log "  [ok] 商店自动更新已禁用 (策略)"
    } else {
        Log "  [!] 商店自动更新策略不存在 —— 这是之前反复弄坏应用的元凶"
        $ans = Read-Host "      现在帮你禁用吗? (y=是 / 其他=保留)"
        if ($ans -eq 'y') {
            if (-not (Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore')) {
                New-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore' -Force | Out-Null
            }
            Set-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore' -Name 'AutoDownload' -Value 2 -Type DWord
            Log "      商店自动更新已禁用"
        }
    }
}

# ---------------- 主流程 ----------------

Write-Banner "一键修复商店应用" ("运行于 " + (Get-Date -Format 'yyyy-MM-dd HH:mm'))
Log ("日志: " + $logFile)

$fixed = 0; $failed = 0

if ($Mode -eq 'status') {
    Log ""
    Log "==== 只查状态, 不做任何修改 ===="
    $cands = Get-BrokenCandidates
    if ($cands.Count -eq 0) {
        Log "  过去 72 小时没有激活失败记录, 系统安静。"
    } else {
        Log "  过去 72 小时有以下应用出现过激活失败:"
        foreach ($c in $cands) {
            if (-not $c.Installed) { Log ("  " + $c.Family + "  失败" + $c.Count + " 次  [已卸载, 无需处理]"); continue }
            if ($c.Framework) { continue }
            $pkg = Get-RegPkg $c.Family
            $r = Test-Activation $pkg
            $now = if ($r.ok) { "[现在正常]" } else { "[现在仍坏!]" }
            Log ("  " + $c.Family + "  失败" + $c.Count + " 次, 最近 " + $c.Last.ToString('MM-dd HH:mm') + "  " + $now)
        }
    }
}
else {
    # ---- auto: 扫描 -> 确认 -> 修复 ----
    Log ""
    Log "==== 扫描最近 72 小时的故障记录 ===="
    $cands = Get-BrokenCandidates
    $realBroken = @()
    foreach ($c in $cands) {
        if (-not $c.Installed) { Log ("  跳过 (已卸载的残留记录): " + $c.FullName); continue }
        if ($c.Framework) { continue }
        $pkg = Get-RegPkg $c.Family
        Log ("  检查: " + $c.Family + " (72h 内失败 " + $c.Count + " 次) ...")
        $r = Test-Activation $pkg
        if ($r.ok) {
            Log "        现在启动正常, 不需要修"
        } else {
            Log ("        现在确实坏 (进程=" + $r.procs + " 错误=" + $r.errs + ") -> 开始修复")
            $realBroken += $c
        }
    }

    if ($realBroken.Count -eq 0) {
        Log ""
        Log "==== 结论: 没有需要修复的应用 ===="
    } else {
        Log ""
        Log ("==== 修复 " + $realBroken.Count + " 个应用 ====")
        foreach ($c in $realBroken) {
            Log ""
            Log ("-- " + $c.Family + " --")
            $res = Repair-App $c.Family
            if ($res -eq 'fixed') { $fixed++ }
            elseif ($res -eq 'need-reinstall') {
                $res2 = Reinstall-App $c.Family
                if ($res2 -eq 'fixed') { $fixed++ } else { $failed++ }
            } else { $failed++ }
        }
    }

    # ---- 指定应用 ----
    if ($Name) {
        Log ""
        Log ("==== 按关键字修复: " + $Name + " ====")
        $hits = Find-AppsByName $Name
        if ($hits.Count -eq 0) {
            Log "  没找到匹配的应用, 试试其他关键字 (如: teams, terminal, codex, 1password, perplexity, snip)"
        } else {
            foreach ($p in $hits) {
                Log ("-- " + $p.PackageFamilyName + " v" + $p.Version + " --")
                $r = Test-Activation $p -keepRunning $true
                if ($r.ok) {
                    Log ("  启动测试正常" + $(if ($r.note) { " (" + $r.note + ")" } else { " (应用已打开, 保持在运行)" }))
                } else {
                    $res = Repair-App $p.PackageFamilyName
                    if ($res -eq 'fixed') { $fixed++ }
                    elseif ($res -eq 'need-reinstall') {
                        $res2 = Reinstall-App $p.PackageFamilyName
                        if ($res2 -eq 'fixed') { $fixed++ } else { $failed++ }
                    } else { $failed++ }
                }
            }
        }
    }

    Environment-Check

    Log ""
    Log ("==== 完成: 修复 " + $fixed + " 个, 失败 " + $failed + " 个 ====")
}

# ---- 收尾: 支持继续修别的应用 ----
Log ""
while ($true) {
    $kw = Read-Host "还有哪个应用打不开? 输入名字关键字回车修复 (直接回车=退出)"
    if (-not $kw) { break }
    $hits = Find-AppsByName $kw
    if ($hits.Count -eq 0) { Log "  没找到, 换个关键字试试"; continue }
    foreach ($p in $hits) {
        Log ("-- " + $p.PackageFamilyName + " --")
        $r = Test-Activation $p -keepRunning $true
        if ($r.ok) {
            Log ("  启动测试正常" + $(if ($r.note) { " (" + $r.note + ")" } else { " (应用已打开)" }))
        } else {
            $res = Repair-App $p.PackageFamilyName
            if ($res -eq 'need-reinstall') { $null = Reinstall-App $p.PackageFamilyName }
        }
    }
}

if ($NoPause) { Log "" } else {
    Log ""
    Read-Host "全部完成, 按回车关闭窗口"
}
