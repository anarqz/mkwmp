$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = Join-Path $workspace 'scratch/game'
$target = Join-Path $workspace 'scratch/game-p2'
$probe = Join-Path $workspace 'native/probe/build/winmm.dll'

if (Test-Path -LiteralPath $target) {
    throw "Player two directory already exists: $target"
}
$files = @('mkw.exe', 'mkwgame.dll', 'mkwgame.id', 'mkw.ini', 'libopus-0.dll', 'SDL3.dll', 'winmm_host.dll')
$assets = @('exefs', 'romfs', 'spirv')
foreach ($name in ($files + $assets)) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $name))) { throw "Missing game input: $name" }
}
if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'Build the probe first.' }

New-Item -ItemType Directory -Path $target | Out-Null
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $target }
foreach ($name in $assets) {
    New-Item -ItemType Junction -Path (Join-Path $target $name) -Target (Join-Path $source $name) | Out-Null
}
Copy-Item -LiteralPath $probe -Destination (Join-Path $target 'winmm.dll')
New-Item -ItemType Directory -Path (Join-Path $target 'profile') | Out-Null
Write-Output "Player two staged at $target; launch with MKWMP_PLAYER=2 and its own MKW_PROFILE_DIR."
