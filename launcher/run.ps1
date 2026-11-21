# Builds the launcher and starts it; arguments pass through:  .\run.ps1 --demo   .\run.ps1 --demo --page settings
# (Only the app is built; build Core first when it changed:  dotnet build src/Mkwmp.Core)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
dotnet build "$root/src/Mkwmp.App" --no-dependencies -v q -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$root/src/Mkwmp.App/bin/Debug/net10.0-windows/mkwmp.exe" @args
