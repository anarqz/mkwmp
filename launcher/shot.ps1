# Renders one state of the launcher (the demo) to a PNG and exits; the window stays off-screen.
#   .\shot.ps1 out.png                                  home at 1920x1080
#   .\shot.ps1 out.png --page settings --size 1280x800 --theme dark
#   .\shot.ps1 out.png --page settings --category voice --press right,down
#   .\shot.ps1 out.png --modal update        (also: available, ready, ready-game, confirm, message, progress)
#   .\shot.ps1 out.png --friends             .\shot.ps1 out.png --toast
# Where the demo goes online (Nextendo Network is the default; src/Mkwmp.App/Design/DesignLauncher.Nextendo.cs):
#   (nothing)                 an install that already played on an MKWMP server: its server, Friends, and home's invitation to Nextendo
#   --demo-offline            Nextendo, nobody signed in: "Sign in with Nextendo to play online"   (+ --demo-mkwmp: the locked "Configure Online")
#   --demo-nextendo           signed in, playing on Nextendo Network          --demo-nextendo-linked   signed in, playing on MKWMP servers
#   --demo-fresh --page setup [--step welcome|online|name|game|done]          the welcome; its Nextendo page waiting for the browser:
#                             --step online --state nextendo-linking; signed in: add --demo-nextendo; after "Create": --step online=created
# More: --battery 93  --input keyboard  --locale pt-BR  --press a,b,x,y,up,down,left,right,start
#   .\shot.ps1 out.png --overlay [--overlay-progress 0.3 | --overlay-return 0.5] [--overlay-frame <png>|none]   (docs/DESIGN.md, section 11)
# -NoBuild skips the build. -BuildTo <folder> builds the app into that folder and runs it from there: use it while a
# launcher is running from bin\ (its files are in use), e.g.  .\shot.ps1 out.png -BuildTo $env:TEMP\mkwmp-shot
param([Parameter(Mandatory)][string]$Out, [switch]$NoBuild, [string]$BuildTo, [Parameter(ValueFromRemainingArguments)][object[]]$Rest)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$bin = if ($BuildTo) { [IO.Path]::GetFullPath($BuildTo, (Get-Location).Path) } else { "$root/src/Mkwmp.App/bin/Debug/net10.0-windows" }
if (-not $NoBuild) {
    if ($BuildTo) { dotnet build "$root/src/Mkwmp.App" -c Debug -o $bin -v q -nologo | Out-Null }
    else { dotnet build "$root/src/Mkwmp.App" --no-dependencies -v q -nologo | Out-Null }
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}
$exe = "$bin/mkwmp.exe"
$full = [IO.Path]::GetFullPath($Out, (Get-Location).Path)
# PowerShell turns  --press right,down  into an array: put the commas back
$rest = @($Rest | ForEach-Object { if ($_ -is [array]) { $_ -join ',' } else { "$_" } })
$run = Start-Process $exe -ArgumentList (@('--demo', '--shot', "`"$full`"") + $rest) -PassThru
if (-not $run.WaitForExit(30000)) { $run.Kill(); throw "the launcher did not exit" }
if (-not (Test-Path $full)) { throw "no screenshot written (see %LOCALAPPDATA%\mkwmp\launcher-ui.log)" }
$full
