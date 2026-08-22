param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$NativeOutput,
    [Parameter(Mandatory = $true)][string]$ResizedOutput,
    [int]$NativeWidth = 720,
    [int]$NativeHeight = 630,
    [int]$ResizedWidth = 1280,
    [int]$ResizedHeight = 720,
    [string]$MaximizedOutput = "",
    [string]$ThemeOutput = ""
)

$captureType = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

public static class LauncherCapture
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public static void Save(IntPtr handle, int width, int height, string output)
    {
        MoveWindow(handle, 120, 100, width, height, true);
        Thread.Sleep(700);
        using (var image = new Bitmap(width, height))
        using (var graphics = Graphics.FromImage(image))
        {
            var dc = graphics.GetHdc();
            PrintWindow(handle, dc, 0);
            graphics.ReleaseHdc(dc);
            image.Save(output, ImageFormat.Png);
        }
    }

    public static void SaveMaximized(IntPtr handle, int width, int height, string output)
    {
        ShowWindow(handle, 3);
        Thread.Sleep(900);
        using (var image = new Bitmap(width, height))
        using (var graphics = Graphics.FromImage(image))
        {
            var dc = graphics.GetHdc();
            PrintWindow(handle, dc, 0);
            graphics.ReleaseHdc(dc);
            image.Save(output, ImageFormat.Png);
        }
    }

    public static void ToggleTheme(IntPtr handle)
    {
        SetForegroundWindow(handle);
        Thread.Sleep(250);
        SetCursorPos(489, 134);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(1100);
    }
}
'@

Add-Type -TypeDefinition $captureType -ReferencedAssemblies System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$process = Start-Process -FilePath $Executable -PassThru
try {
    Start-Sleep -Seconds 3
    [LauncherCapture]::Save($process.MainWindowHandle, $NativeWidth, $NativeHeight, $NativeOutput)
    [LauncherCapture]::Save($process.MainWindowHandle, $ResizedWidth, $ResizedHeight, $ResizedOutput)
    if ($MaximizedOutput) {
        $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        [LauncherCapture]::SaveMaximized($process.MainWindowHandle, $screen.Width, $screen.Height, $MaximizedOutput)
    }
    if ($ThemeOutput) {
        [LauncherCapture]::Save($process.MainWindowHandle, 720, 630, $NativeOutput)
        [LauncherCapture]::ToggleTheme($process.MainWindowHandle)
        [LauncherCapture]::Save($process.MainWindowHandle, 720, 630, $ThemeOutput)
        [LauncherCapture]::ToggleTheme($process.MainWindowHandle)
    }
}
finally {
    if (!$process.HasExited) { Stop-Process -Id $process.Id -Force }
}
