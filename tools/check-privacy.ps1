# 隐私检查：发布前必须过
#
# 检查目标：会被提交到公开仓库的文件（按 .gitignore 白名单思路反过来做）
# 忽略：bin / obj / dist / packages-local / logs / testdata / tools/_* 等本机产物
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\check-privacy.ps1
# 退出码 0 = 干净

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

# 这些目录/文件不会进公开仓库（与 .gitignore 保持一致）
$excludeDirs = @(
    '\\bin\\', '\\obj\\', '\\dist\\', '\\packages-local\\',
    '\\logs\\', '\\testdata\\', '\\tools\\_',
    '\\.git\\', '\\.vs\\', '\\.dotnet_home\\', '\\.nuget\\',
    '\\release\\dist\\'
)

# 压缩包无法做字符串检查：解出来是二进制，随机字节会碰巧匹配敏感词
# （已踩过一次：zip 里的随机数据命中了 PVZ）
$binaryExt = @('.zip', '.7z', '.rar', '.gz', '.bz2', '.xz', '.cab', '.msi')

$files = Get-ChildItem $root -Recurse -File | Where-Object {
    if ($_.Name -eq 'check-privacy.ps1') { return $false }   # 检查脚本自身包含规则，跳过

    $full = $_.FullName
    if ($binaryExt -contains $_.Extension.ToLowerInvariant()) { return $false }
    -not ($excludeDirs | Where-Object { $full -match $_ })
}

$rules = @(
    @{ Name = '本机用户名';     Pattern = 'abc54' },
    @{ Name = '用户目录路径';   Pattern = '[A-Za-z]:\\+Users\\+' },
    @{ Name = '本机测试目录';   Pattern = '非库测试文件夹' },
    @{ Name = '本机账户名';     Pattern = 'GuCun(?!F)|GUCUN(?!F)' },   # GuCunF 是公开的 GitHub 用户名，不算泄露
    @{ Name = '其他项目痕迹';   Pattern = 'NapCat|LangBot|索拉里斯|PVZ|阿里云|腾讯云' },
    @{ Name = '疑似凭据';       Pattern = '(?i)(api[_-]?key|secret[_-]?key|access[_-]?token|BEGIN [A-Z ]*PRIVATE KEY)' },
    @{ Name = '内网/公网 IP';   Pattern = '\b(?:(?:10|192\.168|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\.\d{1,3})\b' }
)

$problems = 0

foreach ($rule in $rules) {
    $hits = $files | Select-String -Pattern $rule.Pattern -List -ErrorAction SilentlyContinue

    if (-not $hits) {
        continue
    }

    Write-Host "命中「$($rule.Name)」$($hits.Count) 个文件：" -ForegroundColor Yellow

    foreach ($hit in $hits) {
        $relative = $hit.Path.Replace($root + '\', '')

        # 逐行找出具体位置，方便修
        $lineNo = 0
        Get-Content $hit.Path -Encoding UTF8 -ErrorAction SilentlyContinue | ForEach-Object {
            $lineNo++
            if ($_ -match $rule.Pattern) {
                Write-Host ("  {0}:{1}  {2}" -f $relative, $lineNo, $_.Trim())
            }
        }
    }

    $problems += $hits.Count
}

if ($problems -eq 0) {
    Write-Host '隐私检查通过：未发现本机路径、用户名、凭据或其他项目痕迹' -ForegroundColor Green
    exit 0
}

Write-Host "发现 $problems 处，发布前必须处理" -ForegroundColor Red
exit 1
