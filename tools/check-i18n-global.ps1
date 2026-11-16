# 全局中英文核对
#
# 三个检查项，和 check-i18n-pairs / check-strings 互补：
#   1. 代码里**硬编码的中文**（没走 UiText.Get 的用户可见文案）
#   2. 未被任何代码引用的词条（可能是废词条，也可能是漏调用）
#   3. 中英同一 key 的长度异常（英文比中文短一半以上，可能漏译）
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\check-i18n-global.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$strings = Join-Path $root 'src\CBox.Core\Strings'

$zh = Get-Content (Join-Path $strings 'strings-zh-CN.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$en = Get-Content (Join-Path $strings 'strings-en-US.json') -Raw -Encoding UTF8 | ConvertFrom-Json

$problems = 0

# ---------- 1. 代码里的硬编码中文 ----------
$hardcoded = @()

Get-ChildItem (Join-Path $root 'src') -Include *.cs, *.xaml -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object {
        $file = $_
        $lineNo = 0

        Get-Content $file.FullName -Encoding UTF8 | ForEach-Object {
            $lineNo++
            $text = $_

            # 跳过注释行
            if ($text.TrimStart().StartsWith('//') -or $text.TrimStart().StartsWith('///')) { return }
            if ($text.TrimStart().StartsWith('<!--')) { return }

            # 只关心"字符串字面量里有中文"
            if ($text -notmatch '"[^"]*[\u4e00-\u9fa5][^"]*"') { return }

            # 排除：日志、异常内部文案、日志字段名、正则、文件后缀等
            if ($text -match 'CbxLog\.') { return }
            # 文件类型的中文是"词条缺失时的兜底"，属于正常保留
            if ($text -match 'new FileKind\(') { return }
            if ($text -match 'EnsureTargetUsable|SafePath|SafeName') { return }

            $hardcoded += [pscustomobject]@{
                File = $file.Name
                Line = $lineNo
                Text = $text.Trim()
            }
        }
    }

if ($hardcoded.Count -gt 0) {
    # 完整清单落盘，便于逐条判断
    $hardcoded | ForEach-Object { "{0}:{1}  {2}" -f $_.File, $_.Line, $_.Text } |
        Set-Content (Join-Path $root 'tools\_i18n-hardcoded.txt') -Encoding UTF8

    Write-Host "代码里仍有中文文案 $($hardcoded.Count) 处（需判断是否用户可见）：" -ForegroundColor Yellow
    $hardcoded | Select-Object -First 30 | ForEach-Object {
        Write-Host ("  {0}:{1}  {2}" -f $_.File, $_.Line, $_.Text)
    }
    if ($hardcoded.Count -gt 30) {
        Write-Host "  … 还有 $($hardcoded.Count - 30) 处"
    }
    $problems += $hardcoded.Count
}

# ---------- 2. 未被引用的词条 ----------
$used = @{}

Get-ChildItem (Join-Path $root 'src') -Include *.cs, *.xaml -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object {
        $text = Get-Content $_.FullName -Raw -Encoding UTF8
        if ([string]::IsNullOrEmpty($text)) { return }

        # 只要代码里出现过 "key" 就算被引用（兼容三元表达式、变量拼接等写法）
        [regex]::Matches($text, '"([a-zA-Z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)"') | ForEach-Object {
            $used[$_.Groups[1].Value] = $true
        }

        [regex]::Matches($text, '\{ui:Tr\s+([A-Za-z0-9_.]+)\s*\}') | ForEach-Object {
            $used[$_.Groups[1].Value] = $true
        }
    }

$allKeys = @($zh.PSObject.Properties.Name | Where-Object { -not $_.StartsWith('_') })
# .pro 变体由代码动态拼接（key + ".pro"），不算未引用
$proKeys = @($allKeys | Where-Object { $_.EndsWith('.pro') })
# 动态拼接前缀：代码里出现 $"pwd.level. 这种写法时，同前缀的词条视为被引用
$dynamicPrefixes = @()
Get-ChildItem (Join-Path $root 'src') -Include *.cs, *.xaml -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object {
        $text = Get-Content $_.FullName -Raw -Encoding UTF8
        if ([string]::IsNullOrEmpty($text)) { return }
        # 动态前缀：$"pwd.level.{key}" 这种插值
        [regex]::Matches($text, '\$"([A-Za-z0-9_.]+)\.\{') | ForEach-Object {
            $dynamicPrefixes += $_.Groups[1].Value + '.'
        }

        # 拼接式前缀："filetype." + code
        [regex]::Matches($text, '"([A-Za-z0-9_.]+)\."\s*\+') | ForEach-Object {
            $dynamicPrefixes += $_.Groups[1].Value + '.'
        }

    }
$unused = @($allKeys | Where-Object {
    if ($used.ContainsKey($_)) { return $false }
    if ($proKeys -contains $_) { return $false }

    foreach ($prefix in $dynamicPrefixes) {
        if ($_ -like "$prefix*") { return $false }
    }

    return $true
})

if ($unused.Count -gt 0) {
    Write-Host "未被引用的词条 $($unused.Count) 条（可能是废词条，也可能漏调用）：" -ForegroundColor Yellow
    $unused | ForEach-Object { Write-Host "  - $_" }
    $problems += $unused.Count
}

# ---------- 3. 中英长度异常 ----------
$tooShort = @()

foreach ($key in $allKeys) {
    $zhText = [string]$zh.$key
    $enText = [string]$en.$key

    if ($zhText.Length -ge 8 -and $enText.Length -lt ($zhText.Length / 3)) {
        $tooShort += "$key  中文($($zhText.Length)) / 英文($($enText.Length))"
    }
}

if ($tooShort.Count -gt 0) {
    Write-Host "英文明显偏短 $($tooShort.Count) 条（可能漏译）：" -ForegroundColor Yellow
    $tooShort | ForEach-Object { Write-Host "  - $_" }
    $problems += $tooShort.Count
}

if ($problems -eq 0) {
    Write-Host '全局中英文核对通过：无硬编码中文、无废词条、无漏译迹象' -ForegroundColor Green
    exit 0
}

Write-Host "共发现 $problems 个待确认项" -ForegroundColor Red
exit 1