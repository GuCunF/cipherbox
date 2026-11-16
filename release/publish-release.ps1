# 发布打包脚本（只产发布版，不碰开发产物）

<#
用途：
    把「发布版」打好包，直接放在 release\dist\ 下，供上传或拷给别人。

与仓库根目录 publish.ps1 的区别：
    - publish.ps1 -All 会同时产出测试版和发布版，给开发自己用
    - 本脚本**只**产出发布版，且把 README / LICENSE / 格式说明一起放进去

用法：
    powershell -ExecutionPolicy Bypass -File release\publish-release.ps1

产物：
    release\dist\CipherBox-<版本>-win-x64\
        CipherBox.exe
        *.dll / *.json
        LICENSE
        README.md
        docs\格式说明.md
#>

$ErrorActionPreference = 'Stop'

$releaseRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $releaseRoot
$appProject = Join-Path $repoRoot 'src\CBox.App\CBox.App.csproj'

# 版本号从 Directory.Build.props 读，避免两处不一致。
# 约定：测试版四段（2.0.0.7），发布版三段（2.0.0）——发布时必须由脚本转成三段。
$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw -Encoding UTF8
$buildVersion = ([regex]::Match($props, '<Version>(.+?)</Version>')).Groups[1].Value

# 四段 → 三段：2.0.0.7 → 2.0.0
$version = ($buildVersion -split '\.' | Select-Object -First 3) -join '.'
$parts = $version -split '\.'
$assemblyVersion = '{0}.{1}.{2}.0' -f $parts[0], $parts[1], $parts[2]

if (-not $version) {
    Write-Host '读不到版本号，打包中止' -ForegroundColor Red
    exit 1
}

$stage = Join-Path $env:TEMP ("cipherbox-release-" + [guid]::NewGuid().ToString('N'))
$output = Join-Path $releaseRoot ("dist\CipherBox-v{0}-win-x64" -f $version)

Write-Host "版本：$version" -ForegroundColor Cyan
Write-Host "输出：$output" -ForegroundColor Cyan

    # 0. 隐私闸门：检查不过就不打包
    $privacyScript = Join-Path $repoRoot 'tools\check-privacy.ps1'

    if (Test-Path $privacyScript) {
        Write-Host '隐私检查…' -ForegroundColor Cyan
        & powershell -NoProfile -ExecutionPolicy Bypass -File $privacyScript

        if ($LASTEXITCODE -ne 0) {
            Write-Host '隐私检查未通过，打包中止' -ForegroundColor Red
            exit 1
        }
    }

try {
    # 1. 编译发布版（TestBuild=false，测试开关全部编译掉）
    # 用 Rebuild 而不是增量：增量构建可能复用之前用测试参数编出来的 Core.dll
    # 那样发布包里就会带着 SlowMode/FailEncryptAtPercent 这类测试符号
    Write-Host '编译发布版…' -ForegroundColor Cyan
    dotnet build $appProject -c Release --nologo -t:Rebuild -p:TestBuild=false -p:Version=$version -p:FileVersion=$version -p:AssemblyVersion=$assemblyVersion -p:DebugType=None -p:DebugSymbols=false -o $stage |
        Select-String -Pattern 'error|已成功|Build succeeded' | ForEach-Object { $_.Line }

    if ($LASTEXITCODE -ne 0) {
        throw '编译失败'
    }

    # 2. 准备输出目录
    if (Test-Path $output) {
        Remove-Item $output -Recurse -Force
    }

    New-Item -ItemType Directory -Force $output | Out-Null

    # 3. 只拷程序文件，不拷 pdb（发布版不需要调试符号）
    Get-ChildItem $stage -File |
        Where-Object { $_.Extension -ne '.pdb' } |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $output $_.Name) -Force }

    # 4. 附上发布用文档
    foreach ($doc in @('LICENSE', 'README.md')) {
        # LICENSE 与 README 以仓库根目录为唯一来源，避免两份内容漂移
        $source = Join-Path $repoRoot $doc

        if (Test-Path $source) {
            Copy-Item $source (Join-Path $output $doc) -Force
        }
        else {
            Write-Host "  缺少 $doc（发布前要补）" -ForegroundColor Yellow
        }
    }

    $docsDir = Join-Path $releaseRoot 'docs'

    if (Test-Path $docsDir) {
        $target = Join-Path $output 'docs'
        New-Item -ItemType Directory -Force $target | Out-Null
        Get-ChildItem $docsDir -File | ForEach-Object { Copy-Item $_.FullName (Join-Path $target $_.Name) -Force }
    }

    # 5. 校验：发布版里不该有测试构建标记
    $dll = Join-Path $output 'CipherBox.dll'
    $text = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($dll))

    if ($text.Contains('测试构建')) {
        Write-Host '警告：产物里出现了「测试构建」标记，可能误打了测试版！' -ForegroundColor Red
    }

    if ($text.Contains('StartFolderEncrypt')) {
        Write-Host '  文件夹/归档功能：已包含' -ForegroundColor Green
    }

    $files = Get-ChildItem $output -File
    Write-Host ("打包完成：{0} 个文件，共 {1:N1} MB" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB)) -ForegroundColor Green
}
finally {
    if (Test-Path $stage) {
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}