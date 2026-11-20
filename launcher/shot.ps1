# Renders one state of the launcher (the demo) to a PNG and exits; the window stays off-screen.
#   .\shot.ps1 out.png                                  home at 1920x1080
#   .\shot.ps1 out.png --page settings --size 1280x800 --theme dark
#   .\shot.ps1 out.png --page settings --category voice --press right,down
#   .\shot.ps1 out.png --modal update        (also: available, confirm, message, progress)
#   .\shot.ps1 out.png --friends             .\shot.ps1 out.png --toast     .\shot.ps1 out.png --demo-offline   (locked online)
# More: --battery 93  --input keyboard  --locale pt-BR  --press a,b,x,y,up,down,left,right,start
#   .\shot.ps1 out.png --overlay [--overlay-progress 0.3 | --overlay-return 0.5] [--overlay-frame <png>|none]   (docs/DESIGN.md, section 11)
# -NoBuild skips the build.
param([Parameter(Mandatory)][string]$Out, [switch]$NoBuild, [Parameter(ValueFromRemainingArguments)][object[]]$Rest)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $NoBuild) {
    dotnet build "$root/src/Mkwmp.App" --no-dependencies -v q -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}
$exe = "$root/src/Mkwmp.App/bin/Debug/net10.0-windows/mkwmp-launcher.exe"
$full = [IO.Path]::GetFullPath($Out, (Get-Location).Path)
# PowerShell turns  --press right,down  into an array: put the commas back
$rest = @($Rest | ForEach-Object { if ($_ -is [array]) { $_ -join ',' } else { "$_" } })
$run = Start-Process $exe -ArgumentList (@('--demo', '--shot', "`"$full`"") + $rest) -PassThru
if (-not $run.WaitForExit(30000)) { $run.Kill(); throw "the launcher did not exit" }
if (-not (Test-Path $full)) { throw "no screenshot written (see %LOCALAPPDATA%\mkwmp\launcher-ui.log)" }
$full
