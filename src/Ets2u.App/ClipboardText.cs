using System.Runtime.InteropServices;

namespace Ets2u.App;

/// <summary>
/// Puts plain text on the clipboard with the Win32 API. WPF's Clipboard goes through OLE, which on a busy
/// clipboard (clipboard history or a clipboard manager reading it) retries for up to a second per call
/// on the UI thread; this waits at most ~200 ms.
/// </summary>
static class ClipboardText
{
    const uint CF_UNICODETEXT = 13;
    const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);

    public static bool TrySet(IntPtr owner, string text)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!OpenClipboard(owner))
            {
                Thread.Sleep(20);
                continue;
            }
            try
            {
                if (!EmptyClipboard()) return false;
                var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((text.Length + 1) * 2));
                if (mem == IntPtr.Zero) return false;
                var p = GlobalLock(mem);
                if (p == IntPtr.Zero) { GlobalFree(mem); return false; }
                Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                Marshal.WriteInt16(p, text.Length * 2, 0);
                GlobalUnlock(mem);
                if (SetClipboardData(CF_UNICODETEXT, mem) != IntPtr.Zero) return true; // the clipboard owns it now
                GlobalFree(mem);
                return false;
            }
            finally
            {
                CloseClipboard();
            }
        }
        return false;
    }
}
