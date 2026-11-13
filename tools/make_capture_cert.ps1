param(
    [string]$OutputDirectory = "$PSScriptRoot/../scratch/certs",
    [string]$HeaderPath = "$PSScriptRoot/../native/probe/src/capture_cert.h",
    # gamesync.npln.nintendo.net: the Gamesync channel's TLS authority (main+0x12696a4).
    [string[]]$DnsName = @('*.lp1.t.npln.srv.nintendo.net', 'gamesync.npln.nintendo.net', 'localhost')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$key = [System.Security.Cryptography.RSA]::Create(2048)
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=mkwmp local capture', $key, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    foreach ($name in $DnsName) { $san.AddDnsName($name) }
    $request.CertificateExtensions.Add($san.Build())
    $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
    $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddYears(10))
    try {
        [IO.File]::WriteAllText((Join-Path $OutputDirectory 'capture-cert.pem'), $cert.ExportCertificatePem())
        [IO.File]::WriteAllText((Join-Path $OutputDirectory 'capture-key.pem'), $key.ExportPkcs8PrivateKeyPem())
        $der = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add('// Generated local capture certificate. Rebuild the probe after regenerating.')
        $lines.Add('static const unsigned char kCaptureCert[] = {')
        for ($i=0; $i -lt $der.Length; $i+=16) {
            $last = [Math]::Min($i+15,$der.Length-1)
            $hex = $der[$i..$last] | ForEach-Object { '0x{0:x2}' -f $_ }
            $lines.Add('    ' + ($hex -join ', ') + ',')
        }
        $lines.Add('};')
        [IO.File]::WriteAllLines($HeaderPath,$lines)
    } finally { $cert.Dispose() }
} finally { $key.Dispose() }
Write-Output "Created local TLS credentials in $OutputDirectory and public certificate header $HeaderPath"
