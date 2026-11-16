# 发布为「刚装好系统也能跑」的自包含单文件。
#
# 关键点：自包含发布需要 win-x64 运行时包，而本机 .NET 的 HTTPS 走 Schannel 是坏的，
# 直接连 nuget.org 会报 NU1301。所以这里用 packages-local 里的离线包做源。
# 离线包缺失时，先跑 tools\fetch-packages.py 重新下载。

# 用法：
#   publish.ps1            只发布测试版到 dist\test
#   publish.ps1 -All       同时发布测试版与发布版（dist\test 与 dist\release）
#   publish.ps1 -Release   只发布发布版到 dist\release
param(
    [switch]$Release,
    [switch]$All
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# 两套产物必须分开目录：同一个 bin 目录会被后一次构建覆盖
$variants = @()
if ($All -or -not $Release) { $variants += @{ Name = 'test'; TestFlag = 'true'; Label = '测试版（CBTEST）' } }
if ($All -or $Release)     { $variants += @{ Name = 'release'; TestFlag = 'false'; Label = '发布版（严格）' } }

$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet_home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget'
$env:DOTNET_NOLOGO = '1'

$feed = Join-Path $root 'packages-local'
if (-not (Test-Path $feed)) {
    throw "缺少离线包目录 packages-local，请先运行：python tools\fetch-packages.py"
}

$targets = @(
    @{ Name = 'enc'; Project = (Join-Path $root 'src\CBox.Encrypt\CBox.Encrypt.csproj'); Exe = 'enc.exe' },
    @{ Name = 'dec'; Project = (Join-Path $root 'src\CBox.Decrypt\CBox.Decrypt.csproj'); Exe = 'dec.exe' },
    @{ Name = 'app'; Project = (Join-Path $root 'src\CBox.App\CBox.App.csproj');         Exe = 'CipherBox.exe' }
)

foreach ($variant in $variants) {
    Write-Host ""
    Write-Host "===== 发布$($variant.Label) ====="

    foreach ($target in $targets) {
        Write-Host "  发布 $($target.Name)"
        dotnet publish $target.Project `
            -c Release `
            -r win-x64 `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=none `
            "-p:TestBuild=$($variant.TestFlag)" `
            --source $feed `
            -o (Join-Path $root "dist\$($variant.Name)\$($target.Name)") `
            --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $versionFile = (Select-String -Path (Join-Path $root 'Directory.Build.props') -Pattern '<Version>(.+?)</Version>').Matches.Groups[1].Value
    $label = if ($variant.Name -eq 'release') { '发布版' } else { '测试版' }
    Set-Content -Path (Join-Path $root "dist\$($variant.Name)\VERSION.txt") -Value "CipherBox $versionFile ($label)`n构建时间 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -Encoding UTF8
}

# 顺带把版本号写进发布目录，方便分辨"这批是不是最新的"
$version = (Select-String -Path (Join-Path $root 'Directory.Build.props') -Pattern '<Version>(.+?)</Version>').Matches.Groups[1].Value
Set-Content -Path (Join-Path $root 'dist\win-x64\VERSION.txt') -Value "CipherBox $version`n构建时间 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -Encoding UTF8

Write-Host ''
Write-Host "发布完成（CipherBox $version）："
foreach ($target in $targets) {
    $exe = Join-Path $root "dist\win-x64\$($target.Name)\$($target.Exe)"
    if (Test-Path $exe) {
        $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
        Write-Host "  $exe  ($size MB)"
    }
}
