# 双槽源码备份 / 恢复
#
# 思路：用 _snap\A 和 _snap\B 两个槽位轮换。
#   - 每次备份：把当前源码存进"另一个"槽，永远保留最近两次状态
#   - 恢复：可以从任一槽取回，取之前会先把当前状态存进临时槽，所以恢复本身也能反悔
#
# 用法：
#   tools\snap.cmd            备份当前源码（轮换 A/B）
#   tools\snap-restore.cmd    从某个槽恢复
#
# 为什么用 PowerShell：cmd 里中文 + 特殊字符很容易因代码页出问题，
# 这里把逻辑放在 .ps1（UTF-8 BOM），.cmd 只做纯 ASCII 启动器。

param(
    [switch]$Restore,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$snapRoot = Join-Path $repoRoot '_snap'
$lastFile = Join-Path $snapRoot 'last.txt'

# 备份范围：源码 + 几个关键配置文件
$fileList = @('Directory.Build.props', 'README.md', 'README.en.md', 'SECURITY.md')
$releaseFiles = @('CipherBox.iss', 'publish-release.ps1')

function Get-SlotState {
    param([string]$Slot)

    $dir = Join-Path $snapRoot $Slot

    if (-not (Test-Path (Join-Path $dir 'src'))) {
        return $null
    }

    $count = (Get-ChildItem $dir -Recurse -File | Measure-Object).Count
    $when = ''

    $whenPath = Join-Path $dir 'when.txt'
    if (Test-Path $whenPath) {
        $when = (Get-Content $whenPath -Raw -Encoding UTF8).Trim()
    }

    return [pscustomobject]@{ Slot = $Slot; Path = $dir; Count = $count; When = $when }
}

function Write-SwappedSlot {
    param([string]$Target)

    $dir = Join-Path $snapRoot $Target

    if (Test-Path $dir) {
        Remove-Item $dir -Recurse -Force
    }

    New-Item -ItemType Directory -Force $dir | Out-Null

    # 源码：排除 bin / obj
    $srcTarget = Join-Path $dir 'src'
    robocopy (Join-Path $repoRoot 'src') $srcTarget /MIR /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null

    foreach ($name in $fileList) {
        $source = Join-Path $repoRoot $name
        if (Test-Path $source) {
            Copy-Item $source (Join-Path $dir $name) -Force
        }
    }

    $relTarget = Join-Path $dir 'release'
    if (Test-Path (Join-Path $repoRoot 'release')) {
        New-Item -ItemType Directory -Force $relTarget | Out-Null
        foreach ($name in $releaseFiles) {
            $source = Join-Path (Join-Path $repoRoot 'release') $name
            if (Test-Path $source) {
                Copy-Item $source (Join-Path $relTarget $name) -Force
            }
        }
    }

    Set-Content (Join-Path $dir 'when.txt') -Value ("{0:yyyy-MM-dd HH:mm:ss}" -f (Get-Date)) -Encoding UTF8
    return $dir
}

function Show-Slots {
    Write-Host ''
    Write-Host '  ===== 备份槽位 =====' -ForegroundColor Cyan
    Write-Host ''

    $last = if (Test-Path $lastFile) { (Get-Content $lastFile -Raw).Trim() } else { '' }

    foreach ($slot in @('A', 'B')) {
        $state = Get-SlotState $slot

        if ($null -eq $state) {
            Write-Host ("    _snap\{0}   空" -f $slot) -ForegroundColor DarkGray
            continue
        }

        $mark = if ($slot -eq $last) { '（最近一次）' } else { '' }
        Write-Host ("    _snap\{0}   {1} 个文件   {2} {3}" -f $slot, $state.Count, $state.When, $mark) -ForegroundColor Green
    }

    Write-Host ''
}

# ---------------- 备份 ----------------

if (-not $Restore) {
    Write-Host ''
    Write-Host '  把当前源码备份到一个槽位（A / B 轮换）' -ForegroundColor Cyan

    if (-not (Test-Path $snapRoot)) {
        New-Item -ItemType Directory -Force $snapRoot | Out-Null
    }

    $last = if (Test-Path $lastFile) { (Get-Content $lastFile -Raw).Trim() } else { '' }
    $target = if ($last -eq 'A') { 'B' } else { 'A' }
    $other = if ($target -eq 'A') { 'B' } else { 'A' }

    Show-Slots

    Write-Host ("  本次写入：_snap\{0}（会覆盖该槽原有内容）" -f $target) -ForegroundColor Yellow
    if (-not $Yes) {
        $answer = Read-Host '  继续？[Y/n]'

        if ($answer -and $answer -notmatch '^[Yy]') {
            Write-Host '  已取消。' -ForegroundColor DarkGray
            return
        }
    }

    Write-Host '  正在复制…' -ForegroundColor Cyan
    $dir = Write-SwappedSlot $target

    Set-Content $lastFile -Value $target -Encoding ASCII

    $state = Get-SlotState $target
    Write-Host ''
    Write-Host ("  [完成] 已备份 {0} 个文件到 _snap\{1}" -f $state.Count, $target) -ForegroundColor Green
    Write-Host ''
    return
}

# ---------------- 恢复 ----------------

Write-Host ''
Write-Host '  从备份恢复源码' -ForegroundColor Cyan

if (-not (Test-Path $snapRoot)) {
    Write-Host '  还没有任何备份，先运行 tools\snap.cmd' -ForegroundColor Red
    return
}

$last = if (Test-Path $lastFile) { (Get-Content $lastFile -Raw).Trim() } else { '' }
$other = if ($last -eq 'A') { 'B' } else { 'A' }

Show-Slots

Write-Host '  要恢复哪一个？' -ForegroundColor Cyan
Write-Host '    [1] 另一个槽（通常是"上一轮"）' -ForegroundColor White
Write-Host '    [2] 最近一次备份' -ForegroundColor White
Write-Host '    [0] 取消' -ForegroundColor DarkGray
Write-Host ''

$choice = Read-Host '  请输入数字'

$slot = switch ($choice) {
    '1' { $other }
    '2' { $last }
    default { $null }
}

if (-not $slot) {
    Write-Host '  已取消。' -ForegroundColor DarkGray
    return
}

$state = Get-SlotState $slot

if ($null -eq $state) {
    Write-Host ("  _snap\{0} 里没有源码，无法恢复。" -f $slot) -ForegroundColor Red
    return
}

Write-Host ''
Write-Host ("  将从 _snap\{0}（{1}，{2} 个文件）覆盖到 src\" -f $slot, $state.When, $state.Count) -ForegroundColor Yellow
Write-Host '  注意：src\ 里当前的内容会被替换。' -ForegroundColor Yellow
if (-not $Yes) {
    $answer = Read-Host '  继续？[y/N]'

    if ($answer -notmatch '^[Yy]') {
        Write-Host '  已取消。' -ForegroundColor DarkGray
        return
    }
}

# 恢复前先把当前状态保险到临时槽，恢复后仍可反悔
$safety = Join-Path $snapRoot 'before-restore'

if (Test-Path $safety) {
    Remove-Item $safety -Recurse -Force
}

Write-Host '  先给当前状态存一份保险…' -ForegroundColor Cyan
robocopy (Join-Path $repoRoot 'src') (Join-Path $safety 'src') /MIR /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null

Write-Host '  正在恢复…' -ForegroundColor Cyan
robocopy (Join-Path $state.Path 'src') (Join-Path $repoRoot 'src') /MIR /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null

foreach ($name in $fileList) {
    $source = Join-Path $state.Path $name
    if (Test-Path $source) {
        Copy-Item $source (Join-Path $repoRoot $name) -Force
    }
}

foreach ($name in $releaseFiles) {
    $source = Join-Path (Join-Path $state.Path 'release') $name
    if (Test-Path $source) {
        Copy-Item $source (Join-Path (Join-Path $repoRoot 'release') $name) -Force
    }
}

Write-Host ''
Write-Host ("  [完成] 已从 _snap\{0} 恢复" -f $slot) -ForegroundColor Green
Write-Host '  建议先跑一次 build.ps1 确认能编译。' -ForegroundColor Yellow
Write-Host ''