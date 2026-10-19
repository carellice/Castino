using System;
using System.Diagnostics;
using System.Threading;

namespace Castino.Mirroring;

/// <summary>
/// Turns mouse wheel / touchpad scrolling into a smooth virtual finger drag on the device.
/// Android apps handle real swipes far more consistently than mouse-wheel axis events (which many apps
/// ignore or scroll by tiny steps). The finger eases towards the accumulated target at ~60 Hz and is
/// lifted only after it has come to rest, so the app does not "fling" unexpectedly.
/// </summary>
public sealed class WheelScroller : IDisposable
{
    // scrcpy's virtual finger id: distinct from the mouse finger so both can coexist briefly.
    private const long PointerId = -3;
    private const int ActionDown = 0, ActionUp = 1, ActionMove = 2;

    private readonly object _lock = new();
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _disposed;

    private IRemoteInput? _input;
    private int _frameW, _frameH;
    private bool _fingerDown;
    private double _anchorX, _anchorY;   // where the gesture started (mouse position)
    private double _x, _y;               // current finger position, frame pixels
    private double _pendingX, _pendingY; // distance still to travel
    private long _lastActivityTicks;

    /// <summary>Distance of one wheel notch as a fraction of the screen height.</summary>
    public double NotchFraction { get; set; } = 0.12;

    public WheelScroller()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "wheel-scroller" };
        _thread.Start();
    }

    /// <param name="notchesX">Positive = scroll right (reveal what is on the right: the finger moves left).</param>
    /// <param name="notchesY">Positive = scroll up (reveal what is above: the finger moves down).</param>
    public void Scroll(IRemoteInput input, int x, int y, int frameW, int frameH, double notchesX, double notchesY)
    {
        lock (_lock)
        {
            if (_fingerDown && (input != _input || frameW != _frameW || frameH != _frameH))
                LiftLocked();

            _input = input;
            _frameW = frameW;
            _frameH = frameH;
            if (!_fingerDown)
            {
                _anchorX = x;
                _anchorY = y;
            }
            double step = frameH * NotchFraction;
            _pendingX += -notchesX * step;
            _pendingY += notchesY * step;
            _lastActivityTicks = Stopwatch.GetTimestamp();
        }
        _wake.Set();
    }

    /// <summary>
    /// Pinch gesture centred on (x, y): two virtual fingers move apart (zoom in, notches &gt; 0) or together.
    /// Runs asynchronously; consecutive calls are serialized.
    /// </summary>
    public void Pinch(IRemoteInput input, int x, int y, int frameW, int frameH, double notches)
    {
        Cancel();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_pinchLock)
            {
                const long idA = -4, idB = -5;
                double min = Math.Min(frameW, frameH);
                double start = notches > 0 ? min * 0.08 : min * 0.22;
                double end = notches > 0 ? min * 0.22 : min * 0.08;
                int cx = Math.Clamp(x, (int)(min * 0.25), frameW - (int)(min * 0.25));
                int cy = Math.Clamp(y, (int)(min * 0.25), frameH - (int)(min * 0.25));

                void Fingers(int action, double half, float pressure)
                {
                    input.Touch(action, idA, cx - (int)half, cy - (int)half, frameW, frameH, pressure);
                    input.Touch(action, idB, cx + (int)half, cy + (int)half, frameW, frameH, pressure);
                }

                Fingers(ActionDown, start / 2, 1f);
                const int steps = 8;
                for (int i = 1; i <= steps; i++)
                {
                    Thread.Sleep(12);
                    Fingers(ActionMove, (start + (end - start) * i / steps) / 2, 1f);
                }
                Thread.Sleep(80); // rest before lifting so the zoom does not keep "flinging"
                Fingers(ActionUp, end / 2, 0f);
            }
        });
    }

    private readonly object _pinchLock = new();

    /// <summary>Ends any scroll gesture right away (call before a real tap).</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _pendingX = _pendingY = 0;
            if (_fingerDown)
                LiftLocked();
        }
    }

    private void Run()
    {
        while (!_disposed)
        {
            bool active;
            lock (_lock)
                active = _fingerDown || Math.Abs(_pendingX) >= 0.5 || Math.Abs(_pendingY) >= 0.5;
            if (!active)
            {
                _wake.WaitOne();
                continue;
            }

            Thread.Sleep(15);
            lock (_lock)
                Tick();
        }
    }

    private void Tick()
    {
        if (_input == null)
            return;

        bool hasPending = Math.Abs(_pendingX) >= 0.5 || Math.Abs(_pendingY) >= 0.5;
        if (!hasPending)
        {
            // Rest a little before lifting: zero velocity at release means no fling.
            double idleMs = Stopwatch.GetElapsedTime(_lastActivityTicks).TotalMilliseconds;
            if (_fingerDown && idleMs > 120)
                LiftLocked();
            return;
        }

        // Android treats a finger that moves less than the "touch slop" (~8 dp) as a tap: never put the
        // finger down for tiny touchpad deltas, accumulate until the gesture is clearly a drag.
        double startThreshold = _frameH * 0.02;
        bool starting = !_fingerDown;
        if (starting)
        {
            if (Math.Max(Math.Abs(_pendingX), Math.Abs(_pendingY)) < startThreshold)
            {
                if (Stopwatch.GetElapsedTime(_lastActivityTicks).TotalMilliseconds > 250)
                    _pendingX = _pendingY = 0; // too small to matter: drop it
                return;
            }
            _x = _anchorX;
            _y = _anchorY;
            _input.Touch(ActionDown, PointerId, (int)_x, (int)_y, _frameW, _frameH, 1f);
            _fingerDown = true;
        }

        // Ease out: move a fraction of the remaining distance each frame (min 2 px).
        double dx = Ease(_pendingX), dy = Ease(_pendingY);
        if (starting)
        {
            // The first move must leave the touch slop at once.
            if (Math.Abs(dx) > 0 && Math.Abs(dx) < startThreshold) dx = Math.Sign(dx) * Math.Min(startThreshold, Math.Abs(_pendingX));
            if (Math.Abs(dy) > 0 && Math.Abs(dy) < startThreshold) dy = Math.Sign(dy) * Math.Min(startThreshold, Math.Abs(_pendingY));
        }
        double nx = _x + dx, ny = _y + dy;

        // Keep the finger on screen: when it would leave the margins, lift and restart from the anchor.
        double mx = _frameW * 0.04, my = _frameH * 0.04;
        if (nx < mx || nx > _frameW - mx || ny < my || ny > _frameH - my)
        {
            LiftLocked();
            _x = _anchorX = Math.Clamp(_anchorX, mx, _frameW - mx);
            _y = _anchorY = Math.Clamp(_anchorY, my, _frameH - my);
            // Start the new stroke from the side opposite to the motion to get a full stroke length.
            if (Math.Abs(dy) >= Math.Abs(dx))
                _y = _anchorY = dy > 0 ? _frameH * 0.2 : _frameH * 0.8;
            else
                _x = _anchorX = dx > 0 ? _frameW * 0.2 : _frameW * 0.8;
            return; // the next tick puts the finger down again
        }

        _x = nx;
        _y = ny;
        _pendingX -= dx;
        _pendingY -= dy;
        _input.Touch(ActionMove, PointerId, (int)_x, (int)_y, _frameW, _frameH, 1f);
        _lastActivityTicks = Stopwatch.GetTimestamp();
    }

    // Move 35% of the remaining distance per tick, at least 8 px so the tail does not crawl
    // (the finger rests before lifting anyway, so the final speed never causes a fling).
    private static double Ease(double remaining)
    {
        if (Math.Abs(remaining) < 0.5)
            return 0;
        double d = remaining * 0.35;
        if (Math.Abs(d) < 8)
            d = Math.Sign(remaining) * Math.Min(8, Math.Abs(remaining));
        return d;
    }

    private void LiftLocked()
    {
        if (_fingerDown)
            _input?.Touch(ActionUp, PointerId, (int)_x, (int)_y, _frameW, _frameH, 0f);
        _fingerDown = false;
    }

    public void Dispose()
    {
        Cancel();
        _disposed = true;
        _wake.Set();
    }
}
