# 代码签名（为 Certum 云签名 / 自签名测试准备）
#
# 用法：
#   # 1. 自签名证书测试整条流程
#   powershell -File release\sign.ps1 -File release\dist\setup-src\CipherBox.exe -SelfTest
#
#   # 2. 用已安装的证书签名（签名后证书会出现在 CurrentUser\My）
#   powershell -File release\sign.ps1 -File xxx.exe -Thumbprint ABCD1234...
#
#   # 3. 按主题名找证书（Certum 装好后通常叫 "CipherBox" 或你的名字）
#   powershell -File release\sign.ps1 -File xxx.exe -Subject "你的名字"
#
# 说明：
#   - 用 PowerShell 自带的 Set-AuthenticodeSignature，不需要装 Windows SDK
#   - Certum 的 SimplySign 云签名会把证书映射到本机证书存储，
#     所以这条路同样适用于拿到 Certum 证书之后
#   - 时间戳服务器建议保留：否则证书到期后，此前签过的文件也会失效

param(
    [Parameter(Mandatory = $true)]
    [string]$File,

    [string]$Thumbprint,
    [string]$Subject,
    [string]$TimestampServer = 'http://timestamp.digicert.com',

    # 建一个临时自签名证书，签完即删，用来验证流程
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $File)) {
    throw "文件不存在：$File"
}

$temporary = $null

try {
    if ($SelfTest) {
        Write-Host '创建临时自签名证书（仅本机可信，用于验证流程）…' -ForegroundColor Cyan

        $temporary = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject 'CN=CipherBox Self Test' `
            -KeyUsage DigitalSignature `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -NotAfter (Get-Date).AddDays(1)

        $cert = Get-Item "Cert:\CurrentUser\My\$($temporary.Thumbprint)"
        Write-Host "  证书指纹：$($temporary.Thumbprint)" -ForegroundColor Gray
    }
    else {
        $cert = $null

        if ($Thumbprint) {
            $cert = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
        }
        elseif ($Subject) {
            $cert = Get-ChildItem 'Cert:\CurrentUser\My' |
                Where-Object { $_.Subject -like "*$Subject*" -and $_.HasPrivateKey } |
                Select-Object -First 1
        }

        if (-not $cert) {
            throw '找不到可用于签名的证书。请检查 -Thumbprint / -Subject，或先装好 Certum 证书。'
        }
    }

    Write-Host "签名：$File" -ForegroundColor Cyan
    Write-Host "证书：$($cert.Subject)" -ForegroundColor Gray

    $result = Set-AuthenticodeSignature `
        -FilePath $File `
        -Certificate $cert `
        -TimestampServer $TimestampServer `
        -HashAlgorithm SHA256

    Write-Host "  状态：$($result.Status)" -ForegroundColor $(if ($result.Status -eq 'Valid') { 'Green' } else { 'Yellow' })

    if ($result.StatusMessage) {
        Write-Host "  说明：$($result.StatusMessage)" -ForegroundColor Yellow
    }

    $verify = Get-AuthenticodeSignature $File

    if ($verify.Status -eq 'Valid' -or $verify.Status -eq 'UnknownError') {
        Write-Host '签名完成（UnknownError 通常表示自签名证书不受信任，属预期）' -ForegroundColor Green
    }
    else {
        Write-Host "签名校验异常：$($verify.Status)" -ForegroundColor Yellow
    }
}
finally {
    if ($temporary) {
        Remove-Item "Cert:\CurrentUser\My\$($temporary.Thumbprint)" -Force -ErrorAction SilentlyContinue
        Write-Host '临时证书已删除' -ForegroundColor Gray
    }
}
