using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Castino.Mirroring;

namespace Castino.Android;

public sealed record ScrcpyOptions
{
    /// <summary>Max of width/height in pixels, 0 = native resolution.</summary>
    public int MaxSize { get; init; } = 1920;
    public int BitRate { get; init; } = 8_000_000;
    public int MaxFps { get; init; } = 60;
    public bool StayAwake { get; init; } = true;
    public bool TurnScreenOff { get; init; }
}

/// <summary>
/// Mirrors and controls an Android device using the scrcpy server (v5.0.1) through adb.
/// Protocol reference: doc/develop.md in the scrcpy repository.
/// </summary>
public sealed class ScrcpySession : IMirrorSession, IRemoteInput
{
    public const string ServerVersion = "5.0.1";
    private const string DeviceServerPath = "/data/local/tmp/castino-scrcpy-server.jar";
    private const uint CodecH264 = 0x68323634;

    private readonly Adb _adb;
    private readonly string _serial;
    private readonly ScrcpyOptions _options;
    private readonly object _sendLock = new();
    private readonly CancellationTokenSource _cts = new();

    private Process? _serverProcess;
    private TcpClient? _videoClient;
    private TcpClient? _controlClient;
    private NetworkStream? _videoStream;
    private NetworkStream? _controlStream;
    private int _localPort;
    private Thread? _videoThread;
    private int _stoppedRaised;
    private readonly StringBuilder _serverLog = new();

    public string DeviceName { get; private set; } = "";
    public IRemoteInput? Input => this;

    public event Action<int, int>? SessionStarted;
    public event Action<byte[], long>? VideoPacket;
    public event Action<string?>? Stopped;

    public ScrcpySession(Adb adb, string serial, ScrcpyOptions options)
    {
        _adb = adb;
        _serial = serial;
        _options = options;
    }

    public async Task StartAsync(IProgress<string>? progress = null)
    {
        string serverFile = Path.Combine(AppContext.BaseDirectory, "tools", "scrcpy-server");
        if (!File.Exists(serverFile))
            throw new FileNotFoundException("scrcpy-server non trovato", serverFile);

        progress?.Report("Invio del server al telefono…");
        AdbResult push = await _adb.RunAsync(new[] { "-s", _serial, "push", serverFile, DeviceServerPath }, TimeSpan.FromSeconds(60));
        if (!push.Success)
            throw new InvalidOperationException("adb push fallito: " + push.Text);

        int scid = Random.Shared.Next(0, int.MaxValue);
        string socketName = $"scrcpy_{scid:x8}";
        _localPort = GetFreeTcpPort();

        AdbResult fwd = await _adb.RunForDeviceAsync(_serial, "forward", $"tcp:{_localPort}", $"localabstract:{socketName}");
        if (!fwd.Success)
            throw new InvalidOperationException("adb forward fallito: " + fwd.Text);

        progress?.Report("Avvio dello streaming…");
        string[] serverArgs =
        {
            "-s", _serial, "shell",
            $"CLASSPATH={DeviceServerPath}", "app_process", "/", "com.genymobile.scrcpy.Server", ServerVersion,
            $"scid={scid:x8}",
            "log_level=info",
            "tunnel_forward=true",
            "audio=false",
            "video_codec=h264",
            $"max_size={_options.MaxSize}",
            $"video_bit_rate={_options.BitRate}",
            $"max_fps={_options.MaxFps}",
            "control=true",
            "clipboard_autosync=false",
            $"stay_awake={(_options.StayAwake ? "true" : "false")}",
            // profile=1 (AVCProfileBaseline): no B-frames, so the Windows decoder outputs each picture
            // immediately instead of holding one frame for reordering (measured: -1 frame of latency).
            // priority=0: real-time encoding.
            "video_codec_options=profile:int=1,priority:int=0",
        };
        _serverProcess = new Process { StartInfo = _adb.CreateStartInfo(serverArgs), EnableRaisingEvents = true };
        _serverProcess.OutputDataReceived += (_, e) => AppendServerLog(e.Data);
        _serverProcess.ErrorDataReceived += (_, e) => AppendServerLog(e.Data);
        _serverProcess.Exited += (_, _) => RaiseStopped(ServerExitMessage());
        _serverProcess.Start();
        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();

        try
        {
            _videoClient = await ConnectWithRetryAsync();
            _videoStream = _videoClient.GetStream();

            // In forward mode the device listens: connect the control socket right after the video one.
            _controlClient = new TcpClient { NoDelay = true };
            await _controlClient.ConnectAsync(IPAddress.Loopback, _localPort, _cts.Token);
            _controlStream = _controlClient.GetStream();

            byte[] meta = new byte[64];
            await ReadExactlyAsync(_videoStream, meta);
            DeviceName = Encoding.UTF8.GetString(meta).TrimEnd('\0');

            byte[] codec = new byte[4];
            await ReadExactlyAsync(_videoStream, codec);
            uint codecId = BinaryPrimitives.ReadUInt32BigEndian(codec);
            if (codecId == 0 || codecId == 1)
                throw new InvalidOperationException("Il telefono non è riuscito ad avviare la cattura dello schermo.\n" + _serverLog);
            if (codecId != CodecH264)
                throw new InvalidOperationException($"Codec inatteso: 0x{codecId:x8}");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }

        // Drain device → client messages (clipboard, uhid) so the socket never fills up.
        _ = Task.Run(DrainControlAsync);

        _videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "scrcpy-video", Priority = ThreadPriority.AboveNormal };
        _videoThread.Start();

        if (_options.TurnScreenOff)
            SetScreenPower(false);
    }

    private async Task<TcpClient> ConnectWithRetryAsync()
    {
        // adb forward accepts the TCP connection even before the server listens on the device:
        // the dummy byte tells us the device side is really connected.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            _cts.Token.ThrowIfCancellationRequested();
            if (_serverProcess is { HasExited: true })
                throw new InvalidOperationException(ServerExitMessage());

            var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20 };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, _localPort, _cts.Token);
                var buf = new byte[1];
                int n = await client.GetStream().ReadAsync(buf, _cts.Token);
                if (n == 1)
                    return client;
            }
            catch (SocketException) { }
            catch (IOException) { }

            client.Dispose();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Il telefono non ha risposto. " + _serverLog);
            await Task.Delay(100, _cts.Token);
        }
    }

    private void VideoLoop()
    {
        var stream = _videoStream!;
        var header = new byte[12];
        byte[]? pendingConfig = null;
        string? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                stream.ReadExactly(header);
                ulong ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(header);

                if ((ptsAndFlags & (1UL << 63)) != 0)
                {
                    // Session packet: a new capture session (e.g. rotation) with its frame size.
                    int width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
                    int height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
                    SessionStarted?.Invoke(width, height);
                    continue;
                }

                int size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
                if (size <= 0 || size > 64 * 1024 * 1024)
                    throw new InvalidDataException("Pacchetto video non valido");

                bool isConfig = (ptsAndFlags & (1UL << 62)) != 0;
                if (isConfig)
                {
                    pendingConfig = new byte[size];
                    stream.ReadExactly(pendingConfig);
                    continue;
                }

                long pts = (long)(ptsAndFlags & ((1UL << 61) - 1));
                byte[] packet;
                if (pendingConfig != null)
                {
                    // Merge SPS/PPS with the following frame, as decoders expect.
                    packet = new byte[pendingConfig.Length + size];
                    pendingConfig.CopyTo(packet, 0);
                    stream.ReadExactly(packet, pendingConfig.Length, size);
                    pendingConfig = null;
                }
                else
                {
                    packet = new byte[size];
                    stream.ReadExactly(packet);
                }
                VideoPacket?.Invoke(packet, pts);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException or SocketException)
        {
            if (!_cts.IsCancellationRequested)
                error = "Connessione con il telefono interrotta.";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        RaiseStopped(error);
    }

    private async Task DrainControlAsync()
    {
        var buf = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested && _controlStream is { } s)
            {
                if (await s.ReadAsync(buf, _cts.Token) == 0)
                    break;
            }
        }
        catch { }
    }

    private void Send(byte[] message)
    {
        var s = _controlStream;
        if (s is null)
            return;
        try
        {
            lock (_sendLock)
                s.Write(message);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    // ---- IRemoteInput ----

    public void Touch(int action, long pointerId, int x, int y, int frameWidth, int frameHeight, float pressure) =>
        Send(ControlMessages.InjectTouch(action, pointerId, x, y, frameWidth, frameHeight, pressure));

    public void Scroll(int x, int y, int frameWidth, int frameHeight, float hScroll, float vScroll) =>
        Send(ControlMessages.InjectScroll(x, y, frameWidth, frameHeight, hScroll, vScroll));

    public void Key(int keycode, int metaState = 0)
    {
        Send(ControlMessages.InjectKeycode(AndroidKeys.ActionDown, keycode, 0, metaState));
        Send(ControlMessages.InjectKeycode(AndroidKeys.ActionUp, keycode, 0, metaState));
    }

    public void Text(string text) => Send(ControlMessages.InjectText(text));

    public void Paste(string text) => Send(ControlMessages.SetClipboard(text, paste: true));

    public void Back()
    {
        Send(ControlMessages.BackOrScreenOn(AndroidKeys.ActionDown));
        Send(ControlMessages.BackOrScreenOn(AndroidKeys.ActionUp));
    }

    public void SetScreenPower(bool on) => Send(ControlMessages.SetDisplayPower(on));

    public void Rotate() => Send(ControlMessages.RotateDevice());

    public void ExpandNotifications() => Send(ControlMessages.ExpandNotificationPanel());

    // ---- lifecycle ----

    private void AppendServerLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        Debug.WriteLine("[scrcpy] " + line);
        lock (_serverLog)
        {
            if (_serverLog.Length < 4000)
                _serverLog.AppendLine(line);
        }
    }

    private string ServerExitMessage()
    {
        lock (_serverLog)
            return _serverLog.Length > 0 ? "Il server sul telefono si è chiuso:\n" + _serverLog : "Il server sul telefono si è chiuso.";
    }

    private void RaiseStopped(string? error)
    {
        if (Interlocked.Exchange(ref _stoppedRaised, 1) == 0 && _videoThread != null)
            Stopped?.Invoke(_cts.IsCancellationRequested ? null : error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested)
            return;

        if (_options.TurnScreenOff)
            SetScreenPower(true);

        _cts.Cancel();
        _videoClient?.Dispose();
        _controlClient?.Dispose();
        try
        {
            if (_serverProcess is { HasExited: false })
                _serverProcess.Kill(true);
        }
        catch { }
        _serverProcess?.Dispose();

        if (_localPort != 0)
        {
            try { await _adb.RunForDeviceAsync(_serial, "forward", "--remove", $"tcp:{_localPort}"); } catch { }
        }
        _videoThread?.Join(2000);
    }

    private static async Task ReadExactlyAsync(NetworkStream s, byte[] buffer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await s.ReadExactlyAsync(buffer, timeout.Token);
    }

    private static int GetFreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
