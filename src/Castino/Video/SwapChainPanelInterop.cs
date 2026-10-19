using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace Castino.Video;

internal static class SwapChainPanelInterop
{
    // ISwapChainPanelNative (Microsoft.UI.Xaml / WinUI 3 flavour, microsoft.ui.xaml.media.dxinterop.h)
    private static readonly Guid IidSwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    public static unsafe void SetSwapChain(SwapChainPanel panel, IntPtr swapChain)
    {
        IntPtr unknown = ((IWinRTObject)panel).NativeObject.ThisPtr;
        Guid iid = IidSwapChainPanelNative;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out IntPtr native));
        try
        {
            // vtable: IUnknown (3 slots), then SetSwapChain(IDXGISwapChain*)
            var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(IntPtr**)native)[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }
}
