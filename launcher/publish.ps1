# Publishes the launcher as it ships (launcher/docs/UPDATES.md, "The release"):
#   <Out>\mkwmp-launcher.exe     single file, self-contained (also the loose asset mkwmp-launcher-x86_64-windows.exe)
#   <Out>\mkwmp-updater.exe      the update helper, Native AOT (needs Visual Studio's C++ x64 tools to link)
#   <Out>\i18n\, licenses\, mkwmp-launcher.png
#   <Out>\game\mkwmp-mod.zip     the MKWMP mod built from this tree (native/probe/build_plugin.py --package, with the
#                                voice and camera libraries), which the launcher installs into mkw-recomp
#   <Out>\game\SHA256SUMS        "# mkwmp <version>" and the zip's hash. The release machine signs it
#                                (tools/release/sign_sums.py -> SHA256SUMS.sig); without the signature the launcher
#                                ignores the folder and downloads the mod from its own release.
#
#   -Dev      a local test build, which cannot be signed: also writes game\DEV-PACKAGE, and client.toml with
#             dev_package = true when there is none. The launcher takes an unsigned game\ ONLY with that key or with
#             MKWMP_DEV_PACKAGE=1 in its environment, and Settings > Game then says "development package (unsigned)".
#             Never ship a -Dev folder.
#   -NoGame   the launcher only (no native build; needs neither zig nor Python).
#   -Zig      zig.exe (Zig 0.15) for the native build; default: zig on PATH. -ZigCache: where zig keeps its caches.
#
#   .\publish.ps1 -Out C:\tmp\mkwmp            .\publish.ps1 -Out C:\tmp\mkwmp -Version 1.5.0 -ArtifactsPath C:\tmp\obj
#   .\publish.ps1 -Out C:\tmp\mkwmp -Dev -Zig C:\tools\zig\zig.exe
param(
    [Parameter(Mandatory)][string]$Out, [string]$Version, [string]$ArtifactsPath,
    [switch]$Dev, [switch]$NoGame, [string]$Zig, [string]$ZigCache, [string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = Split-Path $root -Parent
if ($Dev -and $NoGame) { throw '-Dev makes a development package: it cannot go with -NoGame' }
$common = @('-c', 'Release', '-r', 'win-x64', '-p:DebugType=None', '-p:DebugSymbols=false', '-v', 'q', '-nologo', '-o', $Out)
if ($Version) { $common += "-p:Version=$Version" }
if ($ArtifactsPath) { $common += @('--artifacts-path', $ArtifactsPath) }

dotnet publish "$root/src/Mkwmp.App" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true @common
if ($LASTEXITCODE -ne 0) { throw 'launcher publish failed' }

# The native link looks vswhere.exe up on PATH (as in Infinity Launcher's build.ps1).
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (-not (Test-Path -LiteralPath (Join-Path $vsInstaller 'vswhere.exe'))) { throw 'the update helper (Native AOT) needs Visual Studio with the C++ x64 tools' }
$pathBefore = $env:PATH
$env:PATH = "$vsInstaller;$env:PATH"
try {
    dotnet publish "$root/src/Mkwmp.Updater" @common
    if ($LASTEXITCODE -ne 0) { throw 'update helper publish failed' }
} finally { $env:PATH = $pathBefore }

if (-not $NoGame) {
    # The version the launcher will look for in SHA256SUMS: its own (Directory.Build.props unless -Version says otherwise).
    $launcherVersion = if ($Version) { $Version } else { ([xml](Get-Content -LiteralPath "$root/Directory.Build.props" -Raw)).Project.PropertyGroup.Version }
    if (-not $launcherVersion) { throw 'no launcher version (Directory.Build.props)' }
    $modVersion = $launcherVersion
    if ($Dev) {
        $commit = (& git -C $repo rev-parse --short HEAD 2>$null)
        $modVersion = if ($commit) { "$launcherVersion-dev.$commit" } else { "$launcherVersion-dev" }
    }
    if (-not $Zig) { $Zig = (Get-Command zig -ErrorAction SilentlyContinue).Source }
    if (-not $Zig -or -not (Test-Path -LiteralPath $Zig)) { throw 'the mod needs Zig 0.15: pass -Zig <zig.exe> (or -NoGame for the launcher alone)' }
    $cacheBefore = $env:ZIG_GLOBAL_CACHE_DIR, $env:ZIG_LOCAL_CACHE_DIR
    if ($ZigCache) { $env:ZIG_GLOBAL_CACHE_DIR = $ZigCache; $env:ZIG_LOCAL_CACHE_DIR = $ZigCache }
    try {
        # As .github/workflows/probe-linux.yml lays the mod out: both systems' voice and camera libraries, then the plugin and the zip.
        $steps = @(
            @("$repo/native/voice/build.py", '--zig', $Zig),
            @("$repo/native/voice/build.py", '--zig', $Zig, '--target', 'linux'),
            @("$repo/native/camera/build.py", '--zig', $Zig, '--target', 'windows'),
            @("$repo/native/camera/build.py", '--zig', $Zig, '--target', 'linux'),
            @("$repo/native/probe/build_plugin.py", '--zig', $Zig, '--target', 'all', '--package', '--version', $modVersion))
        foreach ($step in $steps) {
            & $Python -I @step | Select-Object -Last 3
            if ($LASTEXITCODE -ne 0) { throw "native build failed: $($step -join ' ')" }
        }
    } finally { $env:ZIG_GLOBAL_CACHE_DIR, $env:ZIG_LOCAL_CACHE_DIR = $cacheBefore }
    $zip = "$repo/native/probe/build/plugin/mkwmp-mod.zip"
    $game = Join-Path $Out 'game'
    New-Item -ItemType Directory -Force $game | Out-Null
    Get-ChildItem -LiteralPath $game -File | Where-Object Name -in 'mkwmp-mod.zip', 'SHA256SUMS', 'SHA256SUMS.sig', 'DEV-PACKAGE' | Remove-Item -Force -Confirm:$false
    Copy-Item -LiteralPath $zip -Destination (Join-Path $game 'mkwmp-mod.zip')
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $game 'mkwmp-mod.zip')).Hash.ToLowerInvariant()
    # LF, no BOM, two spaces: the bytes the release key signs (tools/release/sign_sums.py writes the same lines).
    [IO.File]::WriteAllText((Join-Path $game 'SHA256SUMS'), "# mkwmp $launcherVersion`n$hash  mkwmp-mod.zip`n", [Text.UTF8Encoding]::new($false))
    if ($Dev) {
        [IO.File]::WriteAllText((Join-Path $game 'DEV-PACKAGE'), "Unsigned development package (mod $modVersion), made by launcher/publish.ps1 -Dev.`nThe launcher uses it only with MKWMP_DEV_PACKAGE=1 or dev_package = true in client.toml. Not for release.`n", [Text.UTF8Encoding]::new($false))
        $config = Join-Path $Out 'client.toml'
        if (-not (Test-Path -LiteralPath $config)) {
            [IO.File]::WriteAllText($config, "# A development build (publish.ps1 -Dev): take the unsigned package in game\.`ndev_package = true`n", [Text.UTF8Encoding]::new($false))
        } elseif (-not (Select-String -LiteralPath $config -Pattern '^\s*dev_package\s*=\s*true' -Quiet)) {
            Write-Warning "$config has no dev_package = true: start the launcher with MKWMP_DEV_PACKAGE=1, or add the key at the top of the file"
        }
        Write-Host "DEVELOPMENT PACKAGE (unsigned): mod $modVersion for launcher $launcherVersion"
    } else {
        Write-Host "game\SHA256SUMS is not signed yet: sign it on the release machine (tools/release/sign_sums.py), or the launcher ignores game\"
    }
}

Get-ChildItem -LiteralPath $Out -Recurse -File | ForEach-Object { '{0,12:N0}  {1}' -f $_.Length, $_.FullName.Substring((Resolve-Path $Out).Path.Length + 1) }
