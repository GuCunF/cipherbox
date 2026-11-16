# 一键构建。
# 把 dotnet 的首次运行目录和 NuGet 包目录都放在项目内部，
# 这样在受限沙盒里也能构建（默认会去写用户目录下的 .dotnet）。

# 用法：
#   build.ps1            构建测试版（CBTEST：日志全开、口令保留、测试开关可见）
#   build.ps1 -Release   构建发布版（严格限制：敏感日志默认关且不可开、无测试开关）
param(
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
# ---- 语言表校验：JSON 坏掉会让界面全是 !key!，宁可构建失败 ----
$strings = Join-Path $PSScriptRoot 'src\CBox.Core\Strings'
foreach ($name in @('zh-CN', 'en-US')) {
    $file = Join-Path $strings "strings-$name.json"

    try {
        $null = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Write-Host "语言表 $name 解析失败，构建中止：" -ForegroundColor Red
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$testFlag = if ($Release) { 'false' } else { 'true' }

$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet_home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget'
$env:DOTNET_NOLOGO = '1'

$projects = @(
    @{ Name = '加密端 enc';   Path = (Join-Path $root 'src\CBox.Encrypt\CBox.Encrypt.csproj'); Out = 'src\CBox.Encrypt\bin\Release\net10.0\enc.exe' },
    @{ Name = '解密端 dec';   Path = (Join-Path $root 'src\CBox.Decrypt\CBox.Decrypt.csproj'); Out = 'src\CBox.Decrypt\bin\Release\net10.0\dec.exe' },
    @{ Name = '图形界面';     Path = (Join-Path $root 'src\CBox.App\CBox.App.csproj');         Out = 'src\CBox.App\bin\Release\net10.0-windows\CipherBox.exe' }
)

foreach ($project in $projects) {
    Write-Host "构建 $($project.Name)"
    dotnet build $project.Path -c Release --nologo "-p:TestBuild=$testFlag"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host ''
if ($Release) { Write-Host '构建完成（发布版，无测试开关）：' } else { Write-Host '构建完成（测试版）：' }
foreach ($project in $projects) {
    $file = Join-Path $root $project.Out
    if (Test-Path $file) { Write-Host "  $($project.Name)  ->  $file" }
}

# ---- 版本号提醒 ----
# 用户在沙箱外测试，靠界面和日志首行的版本号判断"手上是不是最新构建"。
# 判断依据：同一个版本号之前已经构建过，而源码在那之后又变了 —— 说明忘了 +1。
$props = Join-Path $root 'Directory.Build.props'
$stateFile = Join-Path $root '.build-state.txt'
$version = (Select-String -Path $props -Pattern '<Version>(.+?)</Version>').Matches.Groups[1].Value

$newestSource = Get-ChildItem (Join-Path $root 'src') -Recurse -File |
    Where-Object { $_.Extension -in '.cs', '.xaml', '.csproj' -and $_.FullName -notmatch '\\(obj|bin)\\' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

$lastVersion = ''
$lastBuildTime = [datetime]::MinValue
if (Test-Path $stateFile) {
    $lines = Get-Content $stateFile
    if ($lines.Count -ge 2) {
        $lastVersion = $lines[0]
        [datetime]::TryParse($lines[1], [ref]$lastBuildTime) | Out-Null
    }
}

Write-Host ''
Write-Host "当前版本：CipherBox $version"

if ($lastVersion -eq $version -and $newestSource -and $newestSource.LastWriteTime -gt $lastBuildTime) {
    Write-Host "⚠ 版本 $version 已经构建过，但源码之后又改了：$($newestSource.Name)" -ForegroundColor Yellow
    Write-Host "  这次改动要拿出去测试的话，请先把补丁号 +1。" -ForegroundColor Yellow
}

"$version`n$((Get-Date).ToString('o'))" | Set-Content -Path $stateFile -Encoding UTF8
