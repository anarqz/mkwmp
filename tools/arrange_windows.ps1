# Dock the two isolated game windows: P1 (scratch\game) top-left,
# P2 (scratch\game-p2) top-right of the primary work area. Size is unchanged.
# Usage: ./tools/arrange_windows.ps1   (waits up to -TimeoutSec for windows)
param([int]$TimeoutSec = 60)

Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, out RECT r, uint ini);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
'@
[Win32.Native]::SetProcessDPIAware() | Out-Null

function Find-Window([string]$folder) {
    Get-Process mkw -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 -and (Split-Path (Split-Path $_.Path) -Leaf) -eq $folder } |
        Select-Object -First 1
}

$deadline = (Get-Date).AddSeconds($TimeoutSec)
do {
    $p1 = Find-Window 'game'; $p2 = Find-Window 'game-p2'
    if ($p1 -and $p2) { break }
    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)

$work = New-Object Win32.Native+RECT
[Win32.Native]::SystemParametersInfo(0x30, 0, [ref]$work, 0) | Out-Null   # SPI_GETWORKAREA

function Move-Corner($proc, [string]$corner) {
    if (-not $proc) { Write-Output "${corner}: window not found"; return }
    $h = $proc.MainWindowHandle
    $win = New-Object Win32.Native+RECT; $vis = New-Object Win32.Native+RECT
    [Win32.Native]::GetWindowRect($h, [ref]$win) | Out-Null
    # DWMWA_EXTENDED_FRAME_BOUNDS = 9: the visible frame without invisible borders.
    [Win32.Native]::DwmGetWindowAttribute($h, 9, [ref]$vis, 16) | Out-Null
    $padL = $vis.Left - $win.Left; $padR = $win.Right - $vis.Right; $padT = $vis.Top - $win.Top
    $w = $win.Right - $win.Left; $hgt = $win.Bottom - $win.Top
    $x = if ($corner -eq 'top-left') { $work.Left - $padL } else { $work.Right - $w + $padR }
    $y = $work.Top - $padT
    # SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
    [Win32.Native]::SetWindowPos($h, [IntPtr]::Zero, $x, $y, 0, 0, 0x1 -bor 0x4 -bor 0x10) | Out-Null
    Write-Output "${corner}: pid $($proc.Id) -> ($x, $y), ${w}x${hgt}"
}

Move-Corner $p1 'top-left'
Move-Corner $p2 'top-right'
