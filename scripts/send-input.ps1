# Sends real keyboard and mouse-wheel input to a process's window (SendInput), for verifying shortcuts that need
# modifier key state. Brings the window to the foreground first. Keys use virtual-key names: Ctrl+OemPlus, Ctrl+D0.
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)] [string] $ProcessName,
    [string[]] $Keys = @(),
    # Wheel notches to scroll with Ctrl held; positive scrolls up (zoom in).
    [int] $CtrlWheel = 0,
    # Window-relative pixel position for the wheel (from a capture-window.ps1 screenshot).
    [int] $X = 700,
    [int] $Y = 500
)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Input {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Win32Input]::SetProcessDPIAware() | Out-Null
$proc = Get-Process -Name $ProcessName | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
if (-not $proc) { throw "No window for process '$ProcessName'." }
$hwnd = $proc.MainWindowHandle
[Win32Input]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 250

$vk = @{ 'Ctrl' = 0x11; 'Shift' = 0x10; 'OemPlus' = 0xBB; 'OemMinus' = 0xBD; 'D0' = 0x30 }
$KEYUP = 0x2
foreach ($combo in $Keys) {
    $parts = $combo -split '\+'
    foreach ($p in $parts) { [Win32Input]::keybd_event([byte]$vk[$p], 0, 0, [UIntPtr]::Zero) }
    [array]::Reverse($parts)
    foreach ($p in $parts) { [Win32Input]::keybd_event([byte]$vk[$p], 0, $KEYUP, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 120
}

if ($CtrlWheel -ne 0) {
    $rect = New-Object Win32Input+RECT
    [Win32Input]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    [Win32Input]::SetCursorPos($rect.Left + $X, $rect.Top + $Y) | Out-Null
    [Win32Input]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    $notch = if ($CtrlWheel -gt 0) { 120 } else { -120 }
    for ($i = 0; $i -lt [Math]::Abs($CtrlWheel); $i++) {
        [Win32Input]::mouse_event(0x0800, 0, 0, $notch, [UIntPtr]::Zero)   # MOUSEEVENTF_WHEEL
        Start-Sleep -Milliseconds 60
    }
    [Win32Input]::keybd_event(0x11, 0, $KEYUP, [UIntPtr]::Zero)
}
"sent $($Keys.Count) key combos, $CtrlWheel ctrl-wheel notches"
