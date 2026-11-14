# Allow LAN peers (e.g. the Steam Deck) to reach the mkwmp server and the test clients.
# Run once from an elevated PowerShell:  powershell -ExecutionPolicy Bypass -File tools\lan_firewall.ps1
# Private-network profile only. Remove with:  Get-NetFirewallRule -DisplayName 'mkwmp *' | Remove-NetFirewallRule
#Requires -RunAsAdministrator
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$programs = @(
    @{ Name = 'mkwmp server (venv python)'; Path = Join-Path $repo '.venv\Scripts\python.exe' },
    @{ Name = 'mkwmp client P1';            Path = Join-Path $repo 'scratch\game\mkw.exe' },
    @{ Name = 'mkwmp client P2';            Path = Join-Path $repo 'scratch\game-p2\mkw.exe' }
)
# The venv launcher re-executes the base interpreter; allow that one too.
$base = & (Join-Path $repo '.venv\Scripts\python.exe') -c "import sys; print(sys._base_executable)" 2>$null
if ($base) { $programs += @{ Name = 'mkwmp server (base python)'; Path = $base.Trim() } }
foreach ($p in $programs) {
    Get-NetFirewallRule -DisplayName "mkwmp $($p.Name)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName "mkwmp $($p.Name)" -Direction Inbound -Action Allow -Profile Private `
        -Program $p.Path | Out-Null
    "allowed inbound (Private): $($p.Path)"
}
