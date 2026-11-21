# The Windows half of a release (tools/release/release.sh's header): after `release.sh draft` made the draft
# v<version> on the Mac, this builds on Windows what only Windows builds, and uploads it to the draft:
#
#   mkwmp-launcher-x86_64-windows.exe   the launcher as ONE self-extracting file (launcher/publish.ps1 -Loose): what
#                                       every launcher from before the zip (the Rust launcher of 1.3.x and older)
#                                       downloads and swaps for its own exe. Its next update installs the zip.
#   mkwmp-launcher-files-windows.zip    a WORKING FILE of the draft, not an asset: the publish folder but game/
#                                       (mkwmp-launcher.exe, its native *.dll, mkwmp-updater.exe, i18n/, licenses/,
#                                       mkwmp-launcher.png, CHANGELOG.md). make_zips.py packs it into the Windows zip;
#                                       release.sh publish deletes it from the release before publishing.
#   winmm.dll, mkwmp-voice.dll          the game patch and voice chat (older launchers fetch them by name)
#   mkwmp-mod.zip                       the MKWMP mod for mkw-recomp, as publish.ps1 built it into game/
#
#   tools/release/windows.ps1 -Zig C:\tools\zig\zig.exe [-Version 1.4.0] [-Out C:\tmp\rel] [-NoUpload]
#
# Needs: the .NET SDK (launcher/global.json), Visual Studio's C++ x64 tools (the helper's Native AOT link), Zig 0.15,
# Python, and gh signed in to the repository. -Version defaults to launcher/Directory.Build.props, which must be the
# version release.sh drafted. Nothing here signs anything: the release key stays on the Mac.
param([string]$Version, [Parameter(Mandatory)][string]$Zig, [string]$Out, [string]$Python = 'python', [switch]$NoUpload)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$props = ([xml](Get-Content -LiteralPath "$repo/launcher/Directory.Build.props" -Raw)).Project.PropertyGroup.Version
if (-not $Version) { $Version = $props }
if ($Version -ne $props) { throw "-Version $Version is not the launcher's version ($props in launcher/Directory.Build.props)" }
if (-not $Out) { $Out = Join-Path ([IO.Path]::GetTempPath()) "mkwmp-release-$Version" }
if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Force $Out | Out-Null
$folder = Join-Path $Out 'launcher'
$loose = Join-Path $Out 'mkwmp-launcher-x86_64-windows.exe'

# The game patch (winmm.dll); the launcher, its helper, voice chat and the mod (publish.ps1).
& $Python -I "$repo/native/probe/build.py" --zig $Zig --version $Version | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw 'native/probe/build.py failed' }
& "$repo/launcher/publish.ps1" -Out $folder -Version $Version -Loose $loose -Zig $Zig -Python $Python | Out-Null
foreach ($f in 'mkwmp-launcher.exe', 'mkwmp-updater.exe', 'i18n/en.json', 'game/mkwmp-mod.zip') {
    if (-not (Test-Path -LiteralPath (Join-Path $folder $f))) { throw "publish.ps1 left no $f" }
}
if ((Test-Path -LiteralPath (Join-Path $folder 'client.toml')) -or (Test-Path -LiteralPath (Join-Path $folder 'game/DEV-PACKAGE'))) {
    throw "$folder is a development folder (client.toml or game/DEV-PACKAGE): not for a release"
}
Copy-Item -LiteralPath "$repo/native/probe/build/winmm.dll", "$repo/native/voice/build/mkwmp-voice.dll", (Join-Path $folder 'game/mkwmp-mod.zip') -Destination $Out

# The working file: every file of the publish folder but game/, under its relative path with '/' (make_zips.py
# re-packs each entry under mkwmp/ with its fixed date and modes, so nothing of this zip's own metadata ships).
$files = Join-Path $Out 'mkwmp-launcher-files-windows.zip'
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($files, [IO.FileMode]::CreateNew)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $base = (Resolve-Path -LiteralPath $folder).Path.TrimEnd('\') + '\'
        Get-ChildItem -LiteralPath $folder -Recurse -File | Sort-Object FullName | ForEach-Object {
            $rel = $_.FullName.Substring($base.Length).Replace('\', '/')
            if ($rel -like 'game/*') { return }
            $entry = $zip.CreateEntry($rel, [IO.Compression.CompressionLevel]::Optimal)
            $w = $entry.Open()
            try { $r = [IO.File]::OpenRead($_.FullName); try { $r.CopyTo($w) } finally { $r.Dispose() } } finally { $w.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }

$upload = @($loose, $files) + (@('winmm.dll', 'mkwmp-voice.dll', 'mkwmp-mod.zip') | ForEach-Object { Join-Path $Out $_ })
Get-Item -LiteralPath $upload | ForEach-Object { '{0,12:N0}  {1}' -f $_.Length, $_.Name }
if ($NoUpload) { Write-Host "not uploaded (-NoUpload): $Out"; return }
& gh release upload "v$Version" --clobber @upload
if ($LASTEXITCODE -ne 0) { throw "gh release upload v$Version failed (is the draft there? release.sh draft makes it)" }
Write-Host "uploaded to the draft v$Version; next, on the Mac: tools/release/release.sh publish"
