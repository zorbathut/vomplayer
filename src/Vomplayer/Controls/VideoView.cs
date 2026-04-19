using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Vomplayer.Controls;

public class VideoView : NativeControlHost
{
    public event Action<IPlatformHandle>? HandleCreated;

    public IPlatformHandle? NativeHandle { get; private set; }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        NativeHandle = base.CreateNativeControlCore(parent);
        HandleCreated?.Invoke(NativeHandle);
        return NativeHandle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        NativeHandle = null;
        base.DestroyNativeControlCore(control);
    }
}
