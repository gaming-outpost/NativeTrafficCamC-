using Avalonia.Controls;
using Avalonia.Platform;

namespace CoastalCommandCenter.Views;

/// <summary>
/// A <see cref="NativeControlHost"/> that exposes its native X11 window handle
/// so mpv can render into it via --wid.
/// </summary>
public sealed class MpvVideoHost : NativeControlHost
{
    private IPlatformHandle? _handle;

    /// <summary>The native X11 window ID, or 0 if not yet created.</summary>
    public nint NativeWindowId => _handle?.Handle ?? 0;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _handle = base.CreateNativeControlCore(parent);
        return _handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _handle = null;
        base.DestroyNativeControlCore(control);
    }
}
