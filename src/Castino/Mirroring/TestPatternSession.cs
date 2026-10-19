#if DEBUG
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpGen.Runtime;
using Vortice.MediaFoundation;
using ResultCode = Vortice.MediaFoundation.ResultCode;

namespace Castino.Mirroring;

/// <summary>
/// Debug-only fake phone (set CASTINO_TEST_PATTERN=1): encodes a portrait test image with the Windows
/// H.264 encoder and feeds it through the real decoder/renderer. A coloured frame border makes any
/// cropping visible; touches are drawn as dots so input mapping can be checked without a device.
/// </summary>
public sealed class TestPatternSession : IMirrorSession, IRemoteInput
{
    private const int W = 1080, H = 2400;
    private static readonly Guid MtMajor = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MtSub = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MtSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MtRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MtBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MtInterlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MtPar = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MtProfile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

    private readonly CancellationTokenSource _cts = new();
    private readonly List<(int x, int y)> _touches = new();
    private int _scrollOffset;
    private readonly Dictionary<long, int> _lastY = new();
    private Task? _loop;

    public string DeviceName => "Telefono di prova";
    public IRemoteInput? Input => this;
    public event Action<int, int>? SessionStarted;
    public event Action<byte[], long>? VideoPacket;
    public event Action<string?>? Stopped;

    public void Start() => _loop = Task.Run(Run);

    private void Run()
    {
        Guid iid = typeof(IMFTransform).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(new Guid("6CA50344-051A-4DED-9779-A43305165E35"), IntPtr.Zero, 1, ref iid, out IntPtr ptr));
        using var enc = new IMFTransform(ptr);
        using (var o = MediaFactory.MFCreateMediaType())
        {
            o.Set(MtMajor, MediaTypeGuids.Video); o.Set(MtSub, VideoFormatGuids.H264);
            o.Set(MtSize, ((ulong)W << 32) | H); o.Set(MtRate, (30UL << 32) | 1); o.Set(MtPar, (1UL << 32) | 1);
            o.Set(MtBitrate, 6_000_000u); o.Set(MtInterlace, 2u); o.Set(MtProfile, 66u);
            enc.SetOutputType(0, o, 0);
        }
        using (var i = MediaFactory.MFCreateMediaType())
        {
            i.Set(MtMajor, MediaTypeGuids.Video); i.Set(MtSub, VideoFormatGuids.NV12);
            i.Set(MtSize, ((ulong)W << 32) | H); i.Set(MtRate, (30UL << 32) | 1); i.Set(MtPar, (1UL << 32) | 1); i.Set(MtInterlace, 2u);
            enc.SetInputType(0, i, 0);
        }
        enc.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        int outSize = Math.Max(enc.GetOutputStreamInfo(0).Size, W * H);

        SessionStarted?.Invoke(W, H);
        var nv12 = new byte[W * H * 3 / 2];
        long frame = 0;
        while (!_cts.IsCancellationRequested)
        {
            Draw(nv12);
            using (var s = MediaFactory.MFCreateSample())
            using (var b = MediaFactory.MFCreateMemoryBuffer(nv12.Length))
            {
                b.Lock(out IntPtr p, out _, out _); Marshal.Copy(nv12, 0, p, nv12.Length); b.Unlock(); b.CurrentLength = nv12.Length;
                s.AddBuffer(b); s.SampleTime = frame * 333_333; s.SampleDuration = 333_333;
                enc.ProcessInput(0, s, 0);
            }
            while (true)
            {
                using var outSample = MediaFactory.MFCreateSample();
                using (var ob = MediaFactory.MFCreateMemoryBuffer(outSize)) outSample.AddBuffer(ob);
                var odb = new OutputDataBuffer { StreamID = 0, Sample = outSample };
                Result r = enc.ProcessOutput(0, 1, ref odb, out _);
                if (r == ResultCode.TransformNeedMoreInput) break;
                r.CheckError();
                using var buf = outSample.ConvertToContiguousBuffer();
                buf.Lock(out IntPtr p, out _, out int len);
                var data = new byte[len]; Marshal.Copy(p, data, 0, len); buf.Unlock();
                VideoPacket?.Invoke(data, frame * 33_333);
            }
            frame++;
            Thread.Sleep(33);
        }
    }

    private void Draw(byte[] nv12)
    {
        const int border = 16;
        int offset;
        (int x, int y)[] touches;
        lock (_touches) { offset = _scrollOffset; touches = _touches.ToArray(); }
        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            for (int x = 0; x < W; x++)
            {
                byte v;
                if (x < border || y < border || x >= W - border || y >= H - border) v = 235;           // white frame border
                else if (((y + offset) / 150) % 2 == 0) v = 60;                                           // scrolling stripes
                else v = 110;
                nv12[row + x] = v;
            }
        }
        for (int k = W * H; k < nv12.Length; k += 2) { nv12[k] = 90; nv12[k + 1] = 200; }               // red-ish chroma
        foreach (var (tx, ty) in touches)
            for (int y = Math.Max(0, ty - 20); y < Math.Min(H, ty + 20); y++)
                for (int x = Math.Max(0, tx - 20); x < Math.Min(W, tx + 20); x++)
                    nv12[y * W + x] = 255;
    }

    public void Touch(int action, long pointerId, int x, int y, int frameWidth, int frameHeight, float pressure)
    {
        System.Diagnostics.Debug.WriteLine($"[test] touch a={action} id={pointerId} {x},{y} ({frameWidth}x{frameHeight})");
        lock (_touches)
        {
            if (action == 0) _touches.Add((x, y));
            if (_touches.Count > 40) _touches.RemoveAt(0);
            // Content follows the finger, like a scrollable list.
            if (action == 2 && _lastY.TryGetValue(pointerId, out int last)) _scrollOffset -= y - last;
            _lastY[pointerId] = y;
        }
    }

    public void Scroll(int x, int y, int frameWidth, int frameHeight, float hScroll, float vScroll)
    {
        lock (_touches) _scrollOffset -= (int)(vScroll * 100);
    }

    public void Key(int keycode, int metaState = 0) => System.Diagnostics.Debug.WriteLine($"[test] key {keycode} meta={metaState}");
    public void Text(string text) => System.Diagnostics.Debug.WriteLine($"[test] text {text}");
    public void Paste(string text) => System.Diagnostics.Debug.WriteLine($"[test] paste {text}");
    public void Back() => Key(4);
    public void SetScreenPower(bool on) { }
    public void Rotate() { }
    public void ExpandNotifications() { }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null) try { await _loop; } catch { }
        Stopped?.Invoke(null);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, int ctx, ref Guid iid, out IntPtr ppv);
}
#endif
