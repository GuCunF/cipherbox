# 文案一致性检查：确保界面上用到的每个词条都在语言表里
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\check-strings.ps1
# 退出码 0 表示没有问题。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$app = Join-Path $root 'src\CBox.App'
$stringsDir = Join-Path $root 'src\CBox.Core\Strings'

function Get-Keys([string]$file) {
    $json = Get-Content -Path $file -Raw -Encoding UTF8 | ConvertFrom-Json
    return @($json.PSObject.Properties.Name)
}

$problems = 0

# ---- 1. 收集用到的 key ----
$used = @{}

Get-ChildItem $app -Filter *.xaml -Recurse | ForEach-Object {
    $text = Get-Content $_.FullName -Raw -Encoding UTF8
    if ([string]::IsNullOrEmpty($text)) { return }
    [regex]::Matches($text, '\{ui:Tr\s+([A-Za-z0-9_.]+)\s*\}') | ForEach-Object {
        $used[$_.Groups[1].Value] = $_.Groups[1].Value
    }
}

Get-ChildItem (Join-Path $root 'src') -Filter *.cs -Recurse | Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } | ForEach-Object {
    $text = Get-Content $_.FullName -Raw -Encoding UTF8
    if ([string]::IsNullOrEmpty($text)) { return }
    [regex]::Matches($text, 'UiText\.Get\(\s*"([A-Za-z0-9_.]+)"') | ForEach-Object {
        $used[$_.Groups[1].Value] = $_.Groups[1].Value
    }
}

Write-Host "扫描到 $(($used.Keys | Measure-Object).Count) 个不同的词条 key" -ForegroundColor Cyan

# ---- 2. 逐个语言表检查 ----
Get-ChildItem $stringsDir -Filter 'strings-*.json' | ForEach-Object {
    $lang = $_.Name -replace '^strings-', '' -replace '\.json$', ''
    $keys = Get-Keys $_.FullName

    $missing = @($used.Keys | Where-Object { $keys -notcontains $_ })
    if ($missing.Count -gt 0) {
        Write-Host "[$lang] 缺少 $($missing.Count) 条：" -ForegroundColor Yellow
        $missing | ForEach-Object { Write-Host "  - $_" }
        if ($lang -eq 'zh-CN') { $problems += $missing.Count }
    } else {
        Write-Host "[$lang] 词条完整（$($keys.Count) 条）" -ForegroundColor Green
    }
}

# ---- 3. 中文表里的死词条（定义了但没用到）----
$zhKeys = Get-Keys (Join-Path $stringsDir 'strings-zh-CN.json')
$unused = @($zhKeys | Where-Object { $used.Keys -notcontains $_ })
if ($unused.Count -gt 0) {
    Write-Host "中文表里有 $($unused.Count) 条未被引用（可能是留给代码里动态拼的 key）：" -ForegroundColor DarkGray
    $unused | ForEach-Object { Write-Host "  - $_" -ForegroundColor DarkGray }
}

if ($problems -gt 0) {
    Write-Host "发现 $problems 个缺失词条" -ForegroundColor Red
    exit 1
}

Write-Host '文案检查通过' -ForegroundColor Green
exit 0
