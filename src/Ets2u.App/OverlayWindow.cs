using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ets2u.App;

/// <summary>What the overlays share: click-through, never focused, kept out of alt-tab.</summary>
static class OverlayWindow
{
    const int GwlExStyle = -20;
    const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x8000000;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public static void MakeClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate);
    }
}
