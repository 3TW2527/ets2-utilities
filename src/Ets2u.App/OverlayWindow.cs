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
        DrawOnCpu(window);
    }

    /// <summary>Takes clicks (to drag it, press its buttons) but never the focus, so the game keeps its input.</summary>
    public static void MakeNoActivate(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate);
        DrawOnCpu(window);
    }

    /// <summary>
    /// A transparent WPF window drawn on the GPU is copied back to the CPU after every redraw, and that
    /// copy waits for the GPU the game is using: both stutter. These windows are small, so drawing them on
    /// the CPU costs little and leaves the GPU to the game.
    /// </summary>
    static void DrawOnCpu(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source && source.CompositionTarget is { } target)
            target.RenderMode = RenderMode.SoftwareOnly;
    }
}
