# Publishes the launcher as it ships (launcher/docs/UPDATES.md, "The release"):
#   <Out>\mkwmp-launcher.exe     single file, self-contained (also the loose asset mkwmp-launcher-x86_64-windows.exe)
#   <Out>\mkwmp-updater.exe      the update helper, Native AOT (needs Visual Studio's C++ x64 tools to link)
#   <Out>\i18n\, licenses\, mkwmp-launcher.png
#   .\publish.ps1 -Out C:\tmp\mkwmp            .\publish.ps1 -Out C:\tmp\mkwmp -Version 1.5.0 -ArtifactsPath C:\tmp\obj
param([Parameter(Mandatory)][string]$Out, [string]$Version, [string]$ArtifactsPath)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
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

Get-ChildItem -LiteralPath $Out -Recurse -File | ForEach-Object { '{0,12:N0}  {1}' -f $_.Length, $_.FullName.Substring((Resolve-Path $Out).Path.Length + 1) }
