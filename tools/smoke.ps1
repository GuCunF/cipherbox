# -*- coding: utf-8 -*-
# 冒烟测试：正常加解密、错误口令、篡改、截断、信息读取。
# 运行：pwsh -NoProfile -ExecutionPolicy Bypass -File tools\smoke.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$enc = Join-Path $root 'src\CBox.Encrypt\bin\Release\net10.0\enc.exe'
$dec = Join-Path $root 'src\CBox.Decrypt\bin\Release\net10.0\dec.exe'
$work = Join-Path $root 'tools\_smoke'
$pass = 'Smoke-Test-2026'
$results = @()

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $script:results += [pscustomobject]@{ 用例 = $name; 结果 = $(if ($ok) { 'PASS' } else { 'FAIL' }); 说明 = $detail }
}

if (-not (Test-Path $enc)) { throw "找不到加密端：$enc，请先构建" }
if (-not (Test-Path $dec)) { throw "找不到解密端：$dec，请先构建" }

if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null
$srcDir = Join-Path $work 'src'
$outDir = Join-Path $work 'out'
New-Item -ItemType Directory -Force $srcDir | Out-Null
New-Item -ItemType Directory -Force $outDir | Out-Null

# ---- 造测试文件 ----
$textFile = Join-Path $srcDir '报告.txt'
$lines = 1..2000 | ForEach-Object { "第 $_ 行：这是一段用于验证加密解密的中文内容，包含符号 !@#$%^&*() 和数字 1234567890。" }
Set-Content -Path $textFile -Value ($lines -join "`r`n") -Encoding utf8

$binFile = Join-Path $srcDir '随机数据.bin'
$fs = [System.IO.File]::Create($binFile)
$rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
try {
    for ($i = 0; $i -lt 3; $i++) {
        $buf = New-Object byte[] 1048576
        $rng.GetBytes($buf)
        $fs.Write($buf, 0, $buf.Length)
    }
} finally { $fs.Dispose(); $rng.Dispose() }

$emptyFile = Join-Path $srcDir '空文件.dat'
[System.IO.File]::WriteAllBytes($emptyFile, [byte[]]::new(0))

$sources = @($textFile, $binFile, $emptyFile)

# ---- 1. 正常加解密 ----
foreach ($src in $sources) {
    $cbx = $src + '.cbx'
    & $enc $src $cbx -p $pass *> $null
    if ($LASTEXITCODE -ne 0) { Check "加密 $(Split-Path -Leaf $src)" $false "enc 退出码 $LASTEXITCODE"; continue }

    $before = (Get-FileHash -Algorithm SHA256 -Path $src).Hash
    & $dec $cbx $outDir -p $pass *> $null
    if ($LASTEXITCODE -ne 0) { Check "解密 $(Split-Path -Leaf $src)" $false "dec 退出码 $LASTEXITCODE"; continue }

    $restored = Join-Path $outDir (Split-Path -Leaf $src)
    if (-not (Test-Path $restored)) { Check "还原 $(Split-Path -Leaf $src)" $false '输出文件不存在'; continue }

    $after = (Get-FileHash -Algorithm SHA256 -Path $restored).Hash
    Check "往返一致 $(Split-Path -Leaf $src)" ($before -eq $after) "SHA256 前8位 $($before.Substring(0,8))"
}

# ---- 2. 错误口令必须失败，且不留下半成品 ----
$cbxText = $textFile + '.cbx'
$wrongDir = Join-Path $work 'wrong'
New-Item -ItemType Directory -Force $wrongDir | Out-Null
& $dec $cbxText $wrongDir -p 'Wrong-Password-xxx' *> $null
$wrongExit = $LASTEXITCODE
$leftover = @(Get-ChildItem $wrongDir -File -ErrorAction SilentlyContinue)
Check '错误口令被拒绝' ($wrongExit -ne 0) "退出码 $wrongExit"
Check '错误口令不留半成品' ($leftover.Count -eq 0) "残留 $($leftover.Count) 个文件"

# ---- 3. 篡改任意一个字节必须失败 ----
$tampered = Join-Path $work 'tampered.cbx'
Copy-Item $cbxText $tampered
$bytes = [System.IO.File]::ReadAllBytes($tampered)
$offset = [int]($bytes.Length / 2)
$bytes[$offset] = [byte](($bytes[$offset] -bxor 0xFF))
[System.IO.File]::WriteAllBytes($tampered, $bytes)
$tamperDir = Join-Path $work 'tamper'
New-Item -ItemType Directory -Force $tamperDir | Out-Null
& $dec $tampered $tamperDir -p $pass *> $null
Check '篡改被检测' ($LASTEXITCODE -ne 0) "退出码 $LASTEXITCODE"

# ---- 4. 截断必须失败 ----
$truncated = Join-Path $work 'truncated.cbx'
$all = [System.IO.File]::ReadAllBytes($cbxText)
[System.IO.File]::WriteAllBytes($truncated, $all[0..($all.Length - 200)])
$truncDir = Join-Path $work 'trunc'
New-Item -ItemType Directory -Force $truncDir | Out-Null
& $dec $truncated $truncDir -p $pass *> $null
Check '截断被检测' ($LASTEXITCODE -ne 0) "退出码 $LASTEXITCODE"

# ---- 4b. 尾部追加多余数据必须失败 ----
$appended = Join-Path $work 'appended.cbx'
$original = [System.IO.File]::ReadAllBytes($cbxText)
$padded = New-Object byte[] ($original.Length + 16)
[Array]::Copy($original, $padded, $original.Length)
[System.IO.File]::WriteAllBytes($appended, $padded)
$appendDir = Join-Path $work 'append'
New-Item -ItemType Directory -Force $appendDir | Out-Null
& $dec $appended $appendDir -p $pass *> $null
Check '尾部多余数据被检测' ($LASTEXITCODE -ne 0) "退出码 $LASTEXITCODE"

# ---- 5. 头部信息可读 ----
$infoOut = & $dec $cbxText --info 2>&1 | Out-String
Check '头部信息可读' ($LASTEXITCODE -eq 0 -and $infoOut -match 'AES-256-GCM') ''


# ---- 6. 兼容性：1.3 时代的头（名字明文、无类别）仍能正常解密 ----
$compatSrc = Join-Path $work 'compat.txt'
Set-Content -Path $compatSrc -Value '兼容性回归：这是 1.3 结构的文件' -Encoding UTF8
& $enc $compatSrc -p $pass --compat-v1 *> $null
$compatCbx = "$compatSrc.cbx"
$compatFlags = [System.IO.File]::ReadAllBytes($compatCbx)[11]
Check '兼容头不写类别位' (($compatFlags -band 0x04) -eq 0) ("flags=0x{0:X2}" -f $compatFlags)

$compatOut = Join-Path $work 'compat-out'
New-Item -ItemType Directory -Force $compatOut | Out-Null
& $dec $compatCbx -p $pass $compatOut *> $null
$compatText = Get-Content (Join-Path $compatOut 'compat.txt') -Raw -ErrorAction SilentlyContinue
Check '兼容头可解密且内容一致' ($LASTEXITCODE -eq 0 -and $compatText -match '兼容性回归') "退出码 $LASTEXITCODE"

# ---- 7. 全加密：名字不可读、解密后还原 ----
$fullSrc = Join-Path $work '全加密样本.txt'
Set-Content -Path $fullSrc -Value '全加密内容' -Encoding UTF8
& $enc $fullSrc -p $pass --full-encryption *> $null
$fullCbx = "$fullSrc.cbx"

$fullInfo = & $dec $fullCbx --info 2>&1 | Out-String
Check '全加密后名字不可读' ($fullInfo -match '已加密，需口令') ''
Check '全加密类别为其他' ($fullInfo -match '其他') ''

$fullOut = Join-Path $work 'full-out'
New-Item -ItemType Directory -Force $fullOut | Out-Null
& $dec $fullCbx -p $pass $fullOut *> $null
$restored = Test-Path (Join-Path $fullOut '全加密样本.txt')
Check '全加密解密后文件名还原' ($LASTEXITCODE -eq 0 -and $restored) "退出码 $LASTEXITCODE"

# ---- 8. ChaCha20-Poly1305 往返 ----
$algSrc = Join-Path $work 'alg2.bin'
$bytes = New-Object byte[] 262144
(New-Object Random 99).NextBytes($bytes)
[System.IO.File]::WriteAllBytes($algSrc, $bytes)
& $enc $algSrc -p $pass -a 2 *> $null
$algCbx = "$algSrc.cbx"
$algInfo = & $dec $algCbx --info 2>&1 | Out-String
Check 'ChaCha20 头部记录正确' ($algInfo -match 'ChaCha20-Poly1305') ''

$algOut = Join-Path $work 'alg2-out'
New-Item -ItemType Directory -Force $algOut | Out-Null
& $dec $algCbx -p $pass $algOut *> $null
$h1 = (Get-FileHash $algSrc -Algorithm SHA256).Hash
$h2 = (Get-FileHash (Join-Path $algOut 'alg2.bin') -Algorithm SHA256).Hash
Check 'ChaCha20 往返一致' ($LASTEXITCODE -eq 0 -and $h1 -eq $h2) "退出码 $LASTEXITCODE"
# ---- 9. 边界：空文件 ----
$emptySrc = Join-Path $work 'empty.bin'
[System.IO.File]::WriteAllBytes($emptySrc, @())
& $enc $emptySrc -p $pass *> $null
$emptyCbx = "$emptySrc.cbx"
$emptyOut = Join-Path $work 'empty-out'
New-Item -ItemType Directory -Force $emptyOut | Out-Null
& $dec $emptyCbx -p $pass $emptyOut *> $null
$emptySize = (Get-Item (Join-Path $emptyOut 'empty.bin') -ErrorAction SilentlyContinue).Length
Check '空文件往返' ($LASTEXITCODE -eq 0 -and $emptySize -eq 0) "大小 $emptySize"

# ---- 10. 边界：单字节文件 ----
$oneSrc = Join-Path $work 'one.bin'
[System.IO.File]::WriteAllBytes($oneSrc, [byte[]](0x5A))
& $enc $oneSrc -p $pass *> $null
$oneOut = Join-Path $work 'one-out'
New-Item -ItemType Directory -Force $oneOut | Out-Null
& $dec "$oneSrc.cbx" -p $pass $oneOut *> $null
$oneBytes = [System.IO.File]::ReadAllBytes((Join-Path $oneOut 'one.bin'))
Check '单字节文件往返' ($LASTEXITCODE -eq 0 -and $oneBytes.Length -eq 1 -and $oneBytes[0] -eq 0x5A) "长度 $($oneBytes.Length)"

# ---- 11. 中文与特殊字符文件名 ----
$cnSrc = Join-Path $work '中文 名称（含括号）.txt'
Set-Content -Path $cnSrc -Value '中文文件名测试' -Encoding UTF8
& $enc $cnSrc -p $pass *> $null
$cnOut = Join-Path $work 'cn-out'
New-Item -ItemType Directory -Force $cnOut | Out-Null
& $dec "$cnSrc.cbx" -p $pass $cnOut *> $null
$cnRestored = Test-Path (Join-Path $cnOut '中文 名称（含括号）.txt')
Check '中文文件名往返' ($LASTEXITCODE -eq 0 -and $cnRestored) "退出码 $LASTEXITCODE"

# ---- 12. 跨块大文件（3 MB，多于 1 块）----
$bigSrc = Join-Path $work 'big.bin'
$bigBytes = New-Object byte[] 3145728
(New-Object Random 7).NextBytes($bigBytes)
[System.IO.File]::WriteAllBytes($bigSrc, $bigBytes)
& $enc $bigSrc -p $pass *> $null
$bigInfo = & $dec "$bigSrc.cbx" --info 2>&1 | Out-String
$bigOut = Join-Path $work 'big-out'
New-Item -ItemType Directory -Force $bigOut | Out-Null
& $dec "$bigSrc.cbx" -p $pass $bigOut *> $null
$bigHashA = (Get-FileHash $bigSrc -Algorithm SHA256).Hash
$bigHashB = (Get-FileHash (Join-Path $bigOut 'big.bin') -Algorithm SHA256).Hash
Check '跨块大文件往返' ($LASTEXITCODE -eq 0 -and $bigHashA -eq $bigHashB) "3 MiB"
Check '大文件分块数正确' ($bigInfo -match '块数量\s*:\s*3') ' ';

# ---- 13. 快速档（迭代 10 万次）----
$quickSrc = Join-Path $work 'quick.txt'
Set-Content -Path $quickSrc -Value '快速档测试' -Encoding UTF8
& $enc $quickSrc -p $pass --strength quick *> $null
$quickInfo = & $dec "$quickSrc.cbx" --info 2>&1 | Out-String
Check '快速档迭代次数记录' ($quickInfo -match '100,000|100000') ' '
$quickOut = Join-Path $work 'quick-out'
New-Item -ItemType Directory -Force $quickOut | Out-Null
& $dec "$quickSrc.cbx" -p $pass $quickOut *> $null
Check '快速档可解密' ($LASTEXITCODE -eq 0) "退出码 $LASTEXITCODE"

# ---- 14. 输出已存在时默认拒绝覆盖 ----
$dupSrc = Join-Path $work 'dup.txt'
Set-Content -Path $dupSrc -Value '覆盖保护测试' -Encoding UTF8
& $enc $dupSrc -p $pass *> $null
& $enc $dupSrc -p $pass *> $null
Check '默认拒绝覆盖已有密文' ($LASTEXITCODE -ne 0) "退出码 $LASTEXITCODE"
& $enc $dupSrc -p $pass -f *> $null
Check '加 -f 允许覆盖' ($LASTEXITCODE -eq 0) "退出码 $LASTEXITCODE"

# ---- 15. 文件夹归档：加密成一个 .cbx，解出结构与内容一致 ----
$arcSrc = Join-Path $work '归档源'
New-Item -ItemType Directory -Force (Join-Path $arcSrc '子目录') | Out-Null
Set-Content (Join-Path $arcSrc 'a.txt') -Value '归档 A' -Encoding UTF8
Set-Content (Join-Path $arcSrc '子目录\b.txt') -Value '归档 B' -Encoding UTF8
$arcCbx = Join-Path $work '归档.cbx'
& $enc --folder $arcSrc $arcCbx -p $pass *> $null
Check '归档加密成功' ($LASTEXITCODE -eq 0 -and (Test-Path $arcCbx)) "退出码 $LASTEXITCODE"

$arcOut = Join-Path $work '归档解出'
& $dec --folder $arcCbx $arcOut -p $pass *> $null
$arcA = Get-Content (Join-Path $arcOut 'a.txt') -Raw -ErrorAction SilentlyContinue
$arcB = Get-Content (Join-Path $arcOut '子目录\b.txt') -Raw -ErrorAction SilentlyContinue
Check '归档解出根目录文件' ($LASTEXITCODE -eq 0 -and $arcA -match '归档 A') "退出码 $LASTEXITCODE"
Check '归档解出子目录结构' ($null -ne $arcB) ' '

# ---- 16. 文件夹批量：每个文件一个 .cbx ----
$batchOut = Join-Path $work '批量输出'
& $enc --folder $arcSrc $batchOut -p $pass --batch *> $null
$batchFiles = @(Get-ChildItem $batchOut -Filter *.cbx -Recurse -ErrorAction SilentlyContinue)
Check '批量加密生成多个密文' ($LASTEXITCODE -eq 0 -and $batchFiles.Count -ge 2) "生成 $($batchFiles.Count) 个"

$batchBack = Join-Path $work '批量解出'
& $dec --folder $batchOut $batchBack -p $pass --batch *> $null
$batchA = Get-Content (Join-Path $batchBack 'a.txt') -Raw -ErrorAction SilentlyContinue
Check '批量解密还原文件' ($LASTEXITCODE -eq 0 -and $batchA -match '归档 A') "退出码 $LASTEXITCODE"

# ---- 17. 归档预览清单可读 ----
$arcInfo = & $dec $arcCbx --info 2>&1 | Out-String
Check '归档头可读' ($LASTEXITCODE -eq 0 -and $arcInfo -match '归档|目录|archive') ' '

# ---- 报告 ----
$results | Format-Table -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host "全部通过（$($results.Count) 项）" -ForegroundColor Green
    exit 0
} else {
    Write-Host "失败 $($failed.Count) 项" -ForegroundColor Red
    exit 1
}
