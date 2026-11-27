# Certificates for tests/test_nnssl.c's live part (PowerShell 7; nothing is installed, only files are written):
#
#     pwsh native\probe\tests\make_test_ca.ps1 <output folder>
#
#   ca.pem         a test CA (what the client trusts)
#   server.pem     a certificate for ghost.npln.srv.test.invalid and *.wild.test.invalid, issued by that CA
#   server.key     its private key (PKCS#8 PEM)
#   other-ca.pem   another CA that issued nothing: trusting it instead must make the handshake fail
param([Parameter(Mandatory = $true)][string]$Out)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$sha = [System.Security.Cryptography.HashAlgorithmName]::SHA256
$pad = [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
$from = [DateTimeOffset]::UtcNow.AddDays(-1)
$to = [DateTimeOffset]::UtcNow.AddDays(30)

function New-TestCa([string]$Name) {
    $key = [System.Security.Cryptography.RSA]::Create(2048)
    $req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$Name", $key, $sha, $pad)
    $req.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
    $req.CreateSelfSigned($from, $to)
}

$ca = New-TestCa 'MKWMP test CA'
$other = New-TestCa 'MKWMP other test CA'

$key = [System.Security.Cryptography.RSA]::Create(2048)
$req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=ghost test server', $key, $sha, $pad)
$san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
$san.AddDnsName('ghost.npln.srv.test.invalid')
$san.AddDnsName('*.wild.test.invalid')
$req.CertificateExtensions.Add($san.Build())
$serial = [byte[]](1..12 | ForEach-Object { Get-Random -Minimum 1 -Maximum 255 })
$leaf = $req.Create($ca, $from, $to, $serial)

Set-Content -NoNewline -Path (Join-Path $Out 'ca.pem') -Value ($ca.ExportCertificatePem() + "`n")
Set-Content -NoNewline -Path (Join-Path $Out 'other-ca.pem') -Value ($other.ExportCertificatePem() + "`n")
Set-Content -NoNewline -Path (Join-Path $Out 'server.pem') -Value ($leaf.ExportCertificatePem() + "`n")
Set-Content -NoNewline -Path (Join-Path $Out 'server.key') -Value ($key.ExportPkcs8PrivateKeyPem() + "`n")
"wrote ca.pem, other-ca.pem, server.pem, server.key to $Out"
