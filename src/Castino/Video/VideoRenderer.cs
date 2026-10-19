using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Castino.Video;

/// <summary>Axis-aligned rectangle (pixels or DIPs).</summary>
public readonly record struct Box(double X, double Y, double Width, double Height);

/// <summary>
/// Presents decoded NV12 frames into a composition swap chain (hosted by a WinUI SwapChainPanel)
/// using the D3D11 video processor: YUV→RGB conversion and scaling happen on the GPU in one pass,
/// and frames are presented as soon as they are decoded (no queueing, max frame latency 1).
/// </summary>
public sealed class VideoRenderer : IDisposable
{
    private readonly object _lock = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;

    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11VideoProcessorOutputView? _outputView;
    private uint _width = 16, _height = 16;
    private float _scaleX = 1, _scaleY = 1;

    private ID3D11VideoProcessorEnumerator? _vpEnum;
    private ID3D11VideoProcessor? _vp;
    private uint _vpInputWidth, _vpInputHeight;
    private readonly Dictionary<(IntPtr, uint), ID3D11VideoProcessorInputView> _inputViews = new();

    private DecodedFrame? _lastFrame;
    private int _contentWidth, _contentHeight;

    public ID3D11Device Device => _device;

    public VideoRenderer()
    {
        FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            levels, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        _device = device;
        _context = context;

        // The decoder MFT uses the same device from its own calls.
        using (var mt = _device.QueryInterface<ID3D11Multithread>())
            mt.SetMultithreadProtected(true);

        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice1>();
        dxgiDevice.MaximumFrameLatency = 1;
    }

    /// <summary>Creates the swap chain and returns its native pointer for ISwapChainPanelNative::SetSwapChain.</summary>
    public IntPtr CreateSwapChain(uint width, uint height, float scaleX, float scaleY)
    {
        lock (_lock)
        {
            _width = Math.Max(width, 1);
            _height = Math.Max(height, 1);
            _scaleX = scaleX;
            _scaleY = scaleY;

            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var desc = new SwapChainDescription1
            {
                Width = _width,
                Height = _height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
            };
            _swapChain = factory.CreateSwapChainForComposition(_device, desc, null);
            ApplyDpiTransform();
            AcquireBackBuffer();
            DrawLocked();
            return _swapChain.NativePointer;
        }
    }

    /// <summary>Resizes to the panel size in physical pixels; scale is the panel's CompositionScale (DPI).</summary>
    public void Resize(uint width, uint height, float scaleX, float scaleY)
    {
        lock (_lock)
        {
            width = Math.Max(width, 1);
            height = Math.Max(height, 1);
            if (_swapChain == null || (width == _width && height == _height && scaleX == _scaleX && scaleY == _scaleY))
                return;
            _scaleX = scaleX;
            _scaleY = scaleY;
            ApplyDpiTransform();
            if (width != _width || height != _height)
            {
                _width = width;
                _height = height;
                ReleaseBackBuffer();
                _swapChain.ResizeBuffers(2, _width, _height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
                AcquireBackBuffer();
            }
            DrawLocked();
        }
    }

    // SwapChainPanel shows one swap chain pixel per DIP: with display scaling (e.g. 125%) a swap chain
    // sized in physical pixels would be magnified and cropped. The inverse scale maps it back 1:1.
    private void ApplyDpiTransform()
    {
        using var swapChain2 = _swapChain!.QueryInterface<IDXGISwapChain2>();
        swapChain2.MatrixTransform = Matrix3x2.CreateScale(1f / _scaleX, 1f / _scaleY);
    }

    /// <summary>Frame size of the source (e.g. the Android screen as encoded), used to crop decoder padding.</summary>
    public void SetContentSize(int width, int height)
    {
        lock (_lock)
        {
            _contentWidth = width;
            _contentHeight = height;
        }
    }

    /// <summary>Displays a frame immediately. Takes ownership of <paramref name="frame"/>.</summary>
    public void Present(DecodedFrame frame)
    {
        lock (_lock)
        {
            _lastFrame?.Dispose();
            _lastFrame = frame;
            DrawLocked();
        }
    }

    /// <summary>Clears the screen and forgets the last frame (end of a session).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _lastFrame?.Dispose();
            _lastFrame = null;
            ClearInputViews();
            DrawLocked();
        }
    }

    /// <summary>Drops cached input views: decoder surfaces are reallocated after a format change.</summary>
    public void InvalidateInputs()
    {
        lock (_lock)
            ClearInputViews();
    }

    private void AcquireBackBuffer()
    {
        _backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
    }

    private void ReleaseBackBuffer()
    {
        _outputView?.Dispose();
        _outputView = null;
        _backBuffer?.Dispose();
        _backBuffer = null;
    }

    private void DrawLocked()
    {
        if (_swapChain == null || _backBuffer == null)
            return;

        if (_lastFrame == null)
        {
            using ID3D11RenderTargetView rtv = _device.CreateRenderTargetView(_backBuffer);
            _context.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 1f));
            _swapChain.Present(0, PresentFlags.None);
            return;
        }

        Texture2DDescription texDesc = _lastFrame.Texture.Description;
        EnsureVideoProcessor(texDesc.Width, texDesc.Height);

        _outputView ??= _videoDevice.CreateVideoProcessorOutputView(_backBuffer, _vpEnum!,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });

        var key = (_lastFrame.Texture.NativePointer, _lastFrame.ArraySlice);
        if (!_inputViews.TryGetValue(key, out ID3D11VideoProcessorInputView? inputView))
        {
            if (_inputViews.Count > 64)
                ClearInputViews();
            inputView = _videoDevice.CreateVideoProcessorInputView(_lastFrame.Texture, _vpEnum!,
                new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = _lastFrame.ArraySlice },
                });
            _inputViews[key] = inputView;
        }

        int srcW = _contentWidth > 0 ? Math.Min(_contentWidth, (int)texDesc.Width) : (int)texDesc.Width;
        int srcH = _contentHeight > 0 ? Math.Min(_contentHeight, (int)texDesc.Height) : (int)texDesc.Height;
        Box dest = FitRect(srcW, srcH, _width, _height);

        _videoContext.VideoProcessorSetStreamFrameFormat(_vp!, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamSourceRect(_vp!, 0, true, new RawRect(0, 0, srcW, srcH));
        _videoContext.VideoProcessorSetStreamDestRect(_vp!, 0, true,
            new RawRect((int)dest.X, (int)dest.Y, (int)(dest.X + dest.Width), (int)(dest.Y + dest.Height)));
        _videoContext.VideoProcessorSetOutputTargetRect(_vp!, true, new RawRect(0, 0, (int)_width, (int)_height));

        var streams = new[] { new VideoProcessorStream { Enable = true, InputSurface = inputView } };
        _videoContext.VideoProcessorBlt(_vp!, _outputView, 0, 1, streams);
        _swapChain.Present(0, PresentFlags.None);
    }

    private void EnsureVideoProcessor(uint inputWidth, uint inputHeight)
    {
        if (_vp != null && inputWidth == _vpInputWidth && inputHeight == _vpInputHeight)
            return;

        ClearInputViews();
        _outputView?.Dispose();
        _outputView = null;
        _vp?.Dispose();
        _vpEnum?.Dispose();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = inputWidth,
            InputHeight = inputHeight,
            InputFrameRate = new Rational(60, 1),
            OutputWidth = _width,
            OutputHeight = _height,
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.OptimalSpeed,
        };
        _vpEnum = _videoDevice.CreateVideoProcessorEnumerator(content);
        _vp = _videoDevice.CreateVideoProcessor(_vpEnum, 0);
        _vpInputWidth = inputWidth;
        _vpInputHeight = inputHeight;

        // Black bars around the picture; no automatic "enhancements" that would add GPU work.
        _videoContext.VideoProcessorSetOutputBackgroundColor(_vp, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_vp, 0, false);
    }

    private void ClearInputViews()
    {
        foreach (var v in _inputViews.Values)
            v.Dispose();
        _inputViews.Clear();
    }

    /// <summary>Largest rectangle with the content aspect ratio centered in the target.</summary>
    public static Box FitRect(double contentW, double contentH, double targetW, double targetH)
    {
        if (contentW <= 0 || contentH <= 0 || targetW <= 0 || targetH <= 0)
            return new Box(0, 0, targetW, targetH);
        double scale = Math.Min(targetW / contentW, targetH / contentH);
        double w = contentW * scale, h = contentH * scale;
        return new Box((targetW - w) / 2, (targetH - h) / 2, w, h);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _lastFrame?.Dispose();
            _lastFrame = null;
            ClearInputViews();
            ReleaseBackBuffer();
            _vp?.Dispose();
            _vpEnum?.Dispose();
            _swapChain?.Dispose();
            _videoContext.Dispose();
            _videoDevice.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}
