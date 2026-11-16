# 中英对照检查：逐条比对两份语言表
#
# 检查项：
#   1. 两边 key 是否一一对应（缺哪个、多哪个）
#   2. 占位符是否一致（{0} / {1:N0} 数量与顺序）
#   3. 英文表里是否残留中文（lang.restart 刻意双语，白名单）
#   4. 中文表里是否出现"英文字段为空"之类的异常
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\check-i18n-pairs.ps1
# 退出码 0 = 没问题

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dir = Join-Path $root 'src\CBox.Core\Strings'

$zhPath = Join-Path $dir 'strings-zh-CN.json'
$enPath = Join-Path $dir 'strings-en-US.json'

$zh = Get-Content $zhPath -Raw -Encoding UTF8 | ConvertFrom-Json
$en = Get-Content $enPath -Raw -Encoding UTF8 | ConvertFrom-Json

$zhKeys = @($zh.PSObject.Properties.Name | Where-Object { -not $_.StartsWith('_') })
$enKeys = @($en.PSObject.Properties.Name | Where-Object { -not $_.StartsWith('_') })

# 刻意中英并列的词条：英文表里出现中文是正常的
$bilingual = @('lang.restart.title', 'lang.restart.body', 'lang.zh')

$problems = 0

function Placeholders([string]$text) {
    [regex]::Matches($text, '\{(\d+)(:[^}]*)?\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object
}

Write-Host "中文 $(($zhKeys | Measure-Object).Count) 条 / 英文 $(($enKeys | Measure-Object).Count) 条" -ForegroundColor Cyan

# ---- 1. key 对应 ----
$onlyZh = @($zhKeys | Where-Object { $enKeys -notcontains $_ })
$onlyEn = @($enKeys | Where-Object { $zhKeys -notcontains $_ })

if ($onlyZh.Count -gt 0) {
    Write-Host "英文缺少 $($onlyZh.Count) 条：" -ForegroundColor Yellow
    $onlyZh | ForEach-Object { Write-Host "  - $_"; $script:problems++ }
}

if ($onlyEn.Count -gt 0) {
    Write-Host "英文多出 $($onlyEn.Count) 条（中文没有）：" -ForegroundColor Yellow
    $onlyEn | ForEach-Object { Write-Host "  - $_"; $script:problems++ }
}

# ---- 2/3. 逐条比对共有 key ----
$placeholderIssues = @()
$chineseLeft = @()

foreach ($key in $zhKeys) {
    if ($enKeys -notcontains $key) { continue }

    $zhText = [string]$zh.$key
    $enText = [string]$en.$key

    $pz = @(Placeholders $zhText)
    $pe = @(Placeholders $enText)

    if (($pz -join ',') -ne ($pe -join ',')) {
        $placeholderIssues += "{0}  中文({1}) vs 英文({2})" -f $key, ($pz -join ','), ($pe -join ',')
    }

    if ($bilingual -notcontains $key -and $enText -match '[\u4e00-\u9fa5]') {
        $chineseLeft += "$key → $enText"
    }

    if ([string]::IsNullOrWhiteSpace($enText)) {
        $chineseLeft += "$key → （英文为空）"
    }
}

if ($placeholderIssues.Count -gt 0) {
    Write-Host "占位符不一致 $($placeholderIssues.Count) 处：" -ForegroundColor Yellow
    $placeholderIssues | ForEach-Object { Write-Host "  - $_"; $script:problems++ }
}

if ($chineseLeft.Count -gt 0) {
    Write-Host "英文表里还有中文 / 空值 $($chineseLeft.Count) 处：" -ForegroundColor Yellow
    $chineseLeft | ForEach-Object { Write-Host "  - $_"; $script:problems++ }
}

if ($problems -eq 0) {
    Write-Host '中英对照检查通过：key 一一对应、占位符一致、无残留中文' -ForegroundColor Green
    exit 0
}

Write-Host "发现 $problems 个问题" -ForegroundColor Red
exit 1
