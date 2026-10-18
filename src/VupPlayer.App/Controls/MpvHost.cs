using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace VupPlayer.App.Controls;

/// <summary>给 mpv 渲染用的 Win32 子窗口。</summary>
public sealed partial class MpvHost : HwndHost
{
    public nint VideoHandle { get; private set; }

    /// <summary>子窗口创建完成（可以把句柄交给 mpv）。</summary>
    public event Action<nint>? HandleCreated;

    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        VideoHandle = CreateWindowExW(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1, hwndParent.Handle, 0, 0, 0);
        HandleCreated?.Invoke(VideoHandle);
        return new HandleRef(this, VideoHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);
}
