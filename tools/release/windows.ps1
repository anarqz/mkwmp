# The Windows half of a release (tools/release/release.sh's header): after `release.sh draft` made the draft
# v<version> (a test version, X.Y.Z-rc.N: alpha-v<version>) on the Mac (and built the two servers there), this builds on Windows what only Windows builds and
# uploads it to the draft as ONE working file:
#
#   mkwmp-launcher-files-windows.zip    a WORKING FILE of the draft, never an asset: the launcher's publish folder
#                                       (mkwmp.exe, its native *.dll, updater.exe, i18n/, licenses/,
#                                       mkwmp-launcher.png, CHANGELOG.md) and game/ with the game's three files
#                                       (winmm.dll the game patch, mkwmp-voice.dll, mkwmp-mod.zip the mod). make_zips.py
#                                       packs it into mkwmp-<version>-windows-x64.zip and signs game/'s list;
#                                       release.sh publish deletes it from the draft before publishing.
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
# The draft's tag, as release.sh names it: v<version>, or alpha-v<version> for a test version (X.Y.Z-rc.N).
$tag = if ($Version -cmatch '^\d+\.\d+\.\d+$') { "v$Version" } elseif ($Version -cmatch '^\d+\.\d+\.\d+-rc\.(0|[1-9]\d*)$') { "alpha-v$Version" }
       else { throw "version ${Version}: a release is X.Y.Z, a test version X.Y.Z-rc.N" }
if (-not $Out) { $Out = Join-Path ([IO.Path]::GetTempPath()) "mkwmp-release-$Version" }
if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Force $Out | Out-Null
$folder = Join-Path $Out 'launcher'

# The game patch (winmm.dll); the launcher, its helper, voice chat and the mod (publish.ps1).
& $Python -I "$repo/native/probe/build.py" --zig $Zig --version $Version | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw 'native/probe/build.py failed' }
& "$repo/launcher/publish.ps1" -Out $folder -Version $Version -Zig $Zig -Python $Python | Out-Null
foreach ($f in 'mkwmp.exe', 'updater.exe', 'i18n/en.json', 'game/mkwmp-mod.zip') {
    if (-not (Test-Path -LiteralPath (Join-Path $folder $f))) { throw "publish.ps1 left no $f" }
}
if ((Test-Path -LiteralPath (Join-Path $folder 'client.toml')) -or (Test-Path -LiteralPath (Join-Path $folder 'game/DEV-PACKAGE'))) {
    throw "$folder is a development folder (client.toml or game/DEV-PACKAGE): not for a release"
}
# game/ as the release has it: the three files, and no list (publish.ps1's covers the mod alone and is unsigned;
# make_zips.py writes and signs the list of the three).
$game = Join-Path $folder 'game'
Remove-Item -LiteralPath (Join-Path $game 'SHA256SUMS') -Force -Confirm:$false
Copy-Item -LiteralPath "$repo/native/probe/build/winmm.dll", "$repo/native/voice/build/mkwmp-voice.dll" -Destination $game

# The working file: every file of the folder under its relative path with '/' (make_zips.py re-packs each entry under
# mkwmp/ with its fixed date and modes, so nothing of this zip's own metadata ships).
$files = Join-Path $Out 'mkwmp-launcher-files-windows.zip'
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($files, [IO.FileMode]::CreateNew)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $base = (Resolve-Path -LiteralPath $folder).Path.TrimEnd('\') + '\'
        Get-ChildItem -LiteralPath $folder -Recurse -File | Sort-Object FullName | ForEach-Object {
            $rel = $_.FullName.Substring($base.Length).Replace('\', '/')
            $entry = $zip.CreateEntry($rel, [IO.Compression.CompressionLevel]::Optimal)
            $w = $entry.Open()
            try { $r = [IO.File]::OpenRead($_.FullName); try { $r.CopyTo($w) } finally { $r.Dispose() } } finally { $w.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }

Get-Item -LiteralPath $files | ForEach-Object { '{0,12:N0}  {1}' -f $_.Length, $_.Name }
if ($NoUpload) { Write-Host "not uploaded (-NoUpload): $files"; return }
& gh release upload $tag --clobber $files
if ($LASTEXITCODE -ne 0) { throw "gh release upload $tag failed (is the draft there? release.sh draft makes it)" }
Write-Host "uploaded to the draft $tag; next, on the Mac: tools/release/release.sh publish"
