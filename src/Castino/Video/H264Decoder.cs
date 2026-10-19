using System;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using MapFlags = Vortice.Direct3D11.MapFlags;
using ResultCode = Vortice.MediaFoundation.ResultCode;

namespace Castino.Video;

/// <summary>A decoded NV12 picture living in a D3D11 texture (or texture array slice).</summary>
public sealed class DecodedFrame : IDisposable
{
    public ID3D11Texture2D Texture { get; }
    public uint ArraySlice { get; }
    private readonly IDisposable? _owner;

    public DecodedFrame(ID3D11Texture2D texture, uint arraySlice, IDisposable? owner)
    {
        Texture = texture;
        ArraySlice = arraySlice;
        _owner = owner;
    }

    public void Dispose()
    {
        Texture.Dispose();
        _owner?.Dispose();
    }
}

/// <summary>
/// H.264 decoder built on the Windows Media Foundation decoder MFT (hardware DXVA when available),
/// configured for low latency: every input access unit produces its frame immediately.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private static readonly Guid ClsidMsH264DecoderMft = new("62CE7E72-4C71-4d20-B15D-452831A87D9D");
    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid MfSaD3D11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
    private const int MftOutputStreamProvidesSamples = 0x100;
    private const int MftOutputStreamCanProvideSamples = 0x200;

    private static readonly byte[] AccessUnitDelimiter = { 0, 0, 0, 1, 0x09, 0xF0 };

    private static bool s_mfStarted;

    private readonly ID3D11Device _device;
    private readonly IMFTransform _transform;
    private readonly IMFDXGIDeviceManager? _deviceManager;
    private bool _transformProvidesSamples;
    private int _outputBufferSize;
    private uint _frameWidth, _frameHeight;

    // Software fallback: CPU NV12 → staging texture → default texture.
    private ID3D11Texture2D? _staging;
    private ID3D11Texture2D? _uploadTarget;

    public bool IsHardwareAccelerated { get; }

    /// <summary>Raised when the output format changes (resolution / rotation): cached views must be dropped.</summary>
    public event Action? FormatChanged;

    public H264Decoder(ID3D11Device device)
    {
        _device = device;
        if (!s_mfStarted)
        {
            MediaFactory.MFStartup(false).CheckError();
            s_mfStarted = true;
        }

        _transform = CreateTransform();

        using (IMFAttributes attrs = _transform.Attributes)
        {
            attrs.Set(MfLowLatency, 1u);

            bool d3dAware = attrs.GetUInt32(MfSaD3D11Aware, out uint aware).Success && aware != 0;
            if (d3dAware)
            {
                try
                {
                    _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
                    _deviceManager.ResetDevice(device).CheckError();
                    _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)_deviceManager.NativePointer);
                    IsHardwareAccelerated = true;
                }
                catch (SharpGenException)
                {
                    _deviceManager?.Dispose();
                    _deviceManager = null;
                    _transform.ProcessMessage(TMessageType.MessageSetD3DManager, UIntPtr.Zero);
                }
            }
        }

        using (IMFMediaType input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MfMtMajorType, MediaTypeGuids.Video);
            input.Set(MfMtSubtype, VideoFormatGuids.H264);
            _transform.SetInputType(0, input, 0);
        }
        SelectOutputType();

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private static IMFTransform CreateTransform()
    {
        Guid iid = typeof(IMFTransform).GUID;
        int hr = CoCreateInstance(ClsidMsH264DecoderMft, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out IntPtr ptr);
        Marshal.ThrowExceptionForHR(hr);
        return new IMFTransform(ptr);
    }

    private void SelectOutputType()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(0, i);
            }
            catch (SharpGenException)
            {
                throw new NotSupportedException("Il decoder H.264 non supporta l'uscita NV12.");
            }

            using (type)
            {
                if (type.GetGUID(MfMtSubtype) != VideoFormatGuids.NV12)
                    continue;
                _transform.SetOutputType(0, type, 0);
                ulong packed = type.GetUInt64(MfMtFrameSize);
                _frameWidth = (uint)(packed >> 32);
                _frameHeight = (uint)(packed & 0xFFFFFFFF);
                break;
            }
        }

        OutputStreamInfo info = _transform.GetOutputStreamInfo(0);
        _transformProvidesSamples = (info.Flags & (MftOutputStreamProvidesSamples | MftOutputStreamCanProvideSamples)) != 0;
        _outputBufferSize = info.Size;

        _staging?.Dispose();
        _staging = null;
        _uploadTarget?.Dispose();
        _uploadTarget = null;
        FormatChanged?.Invoke();
    }

    /// <summary>Decodes one Annex-B access unit. <paramref name="onFrame"/> receives ownership of each output frame.</summary>
    public void Decode(byte[] data, long ptsMicros, Action<DecodedFrame> onFrame)
    {
        int length = data.Length + AccessUnitDelimiter.Length;
        using (IMFSample sample = MediaFactory.MFCreateSample())
        using (IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(length))
        {
            buffer.Lock(out IntPtr dst, out _, out _);
            Marshal.Copy(data, 0, dst, data.Length);
            // A trailing access unit delimiter tells the decoder the picture is complete, so it outputs it
            // right away instead of waiting for the next packet (saves one full frame of latency).
            Marshal.Copy(AccessUnitDelimiter, 0, dst + data.Length, AccessUnitDelimiter.Length);
            buffer.Unlock();
            buffer.CurrentLength = length;
            sample.AddBuffer(buffer);
            sample.SampleTime = ptsMicros * 10;

            try
            {
                _transform.ProcessInput(0, sample, 0);
            }
            catch (SharpGenException)
            {
                // MF_E_NOTACCEPTING: drain pending output, then retry once.
                DrainOutput(onFrame);
                _transform.ProcessInput(0, sample, 0);
            }
        }
        DrainOutput(onFrame);
    }

    private void DrainOutput(Action<DecodedFrame> onFrame)
    {
        while (true)
        {
            IMFSample? provided = null;
            if (!_transformProvidesSamples)
            {
                provided = MediaFactory.MFCreateSample();
                IMFMediaBuffer mb = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                provided.AddBuffer(mb);
                mb.Dispose();
            }

            var output = new OutputDataBuffer { StreamID = 0, Sample = provided! };
            Result r = _transform.ProcessOutput(0, 1, ref output, out _);
            output.Events?.Dispose();

            if (r == ResultCode.TransformNeedMoreInput)
            {
                provided?.Dispose();
                return;
            }
            if (r == ResultCode.TransformStreamChange)
            {
                provided?.Dispose();
                SelectOutputType();
                continue;
            }
            if (r.Failure)
            {
                provided?.Dispose();
                r.CheckError();
            }

            IMFSample outSample = output.Sample;
            DecodedFrame? frame = IsHardwareAccelerated ? WrapGpuSample(outSample) : UploadCpuSample(outSample);
            if (frame != null)
                onFrame(frame);
        }
    }

    private static DecodedFrame? WrapGpuSample(IMFSample sample)
    {
        using IMFMediaBuffer buffer = sample.GetBufferByIndex(0);
        using IMFDXGIBuffer dxgi = buffer.QueryInterface<IMFDXGIBuffer>();
        IntPtr texPtr = dxgi.GetResource(typeof(ID3D11Texture2D).GUID);
        // The sample keeps the decoder surface alive while the frame is displayed.
        return new DecodedFrame(new ID3D11Texture2D(texPtr), dxgi.SubresourceIndex, sample);
    }

    private DecodedFrame? UploadCpuSample(IMFSample sample)
    {
        try
        {
            if (_staging == null)
            {
                var desc = new Texture2DDescription
                {
                    Width = _frameWidth,
                    Height = _frameHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Write,
                };
                _staging = _device.CreateTexture2D(desc);
                desc.Usage = ResourceUsage.Default;
                desc.CPUAccessFlags = CpuAccessFlags.None;
                desc.BindFlags = BindFlags.ShaderResource;
                _uploadTarget = _device.CreateTexture2D(desc);
            }

            using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out IntPtr src, out _, out _);
            try
            {
                int srcPitch = (int)_frameWidth;
                int rows = (int)_frameHeight;
                ID3D11DeviceContext ctx = _device.ImmediateContext;
                MappedSubresource map = ctx.Map(_staging, 0, MapMode.Write, MapFlags.None);
                unsafe
                {
                    byte* s = (byte*)src;
                    byte* d = (byte*)map.DataPointer;
                    int dstPitch = (int)map.RowPitch;
                    for (int y = 0; y < rows; y++)
                        Buffer.MemoryCopy(s + y * srcPitch, d + y * dstPitch, dstPitch, srcPitch);
                    byte* sUv = s + srcPitch * rows;
                    byte* dUv = d + dstPitch * rows;
                    for (int y = 0; y < rows / 2; y++)
                        Buffer.MemoryCopy(sUv + y * srcPitch, dUv + y * dstPitch, dstPitch, srcPitch);
                }
                ctx.Unmap(_staging, 0);
                ctx.CopyResource(_uploadTarget!, _staging);
            }
            finally
            {
                buffer.Unlock();
            }

            // QueryInterface hands out a separately ref-counted wrapper owned by the frame.
            return new DecodedFrame(_uploadTarget!.QueryInterface<ID3D11Texture2D>(), 0, null);
        }
        finally
        {
            sample.Dispose();
        }
    }

    public void Dispose()
    {
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        _transform.Dispose();
        _deviceManager?.Dispose();
        _staging?.Dispose();
        _uploadTarget?.Dispose();
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, int clsContext, ref Guid iid, out IntPtr ppv);
}
