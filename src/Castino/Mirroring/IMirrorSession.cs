using System;
using System.Threading.Tasks;

namespace Castino.Mirroring;

/// <summary>
/// A screen source that produces an H.264 Annex-B elementary stream.
/// Android (scrcpy) implements it today; an iOS source (ReplayKit companion app or AirPlay receiver)
/// can implement the same interface and reuse the decoder and renderer unchanged.
/// </summary>
public interface IMirrorSession : IAsyncDisposable
{
    string DeviceName { get; }

    /// <summary>Raised (on the network thread) when a new capture session starts: frame size in pixels.</summary>
    event Action<int, int>? SessionStarted;

    /// <summary>Raised (on the network thread) for each H.264 access unit, Annex-B, with config (SPS/PPS) merged in.</summary>
    event Action<byte[], long>? VideoPacket;

    /// <summary>Raised once when the stream ends; the argument is an error message or null for a clean stop.</summary>
    event Action<string?>? Stopped;

    /// <summary>Remote input, or null when the platform does not allow controlling the device (iOS).</summary>
    IRemoteInput? Input { get; }
}

/// <summary>Input injection into the mirrored device. Coordinates are in video frame pixels.</summary>
public interface IRemoteInput
{
    void Touch(int action, long pointerId, int x, int y, int frameWidth, int frameHeight, float pressure);
    void Scroll(int x, int y, int frameWidth, int frameHeight, float hScroll, float vScroll);
    void Key(int keycode, int metaState = 0);
    void Text(string text);
    void Paste(string text);
    void Back();
    void SetScreenPower(bool on);
    void Rotate();
    void ExpandNotifications();
}
