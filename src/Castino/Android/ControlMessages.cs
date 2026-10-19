using System;
using System.Buffers.Binary;
using System.Text;

namespace Castino.Android;

/// <summary>Android constants used by the scrcpy control protocol.</summary>
public static class AndroidKeys
{
    public const int ActionDown = 0;
    public const int ActionUp = 1;
    public const int ActionMove = 2;

    public const int Home = 3;
    public const int Back = 4;
    public const int DpadUp = 19;
    public const int DpadDown = 20;
    public const int DpadLeft = 21;
    public const int DpadRight = 22;
    public const int VolumeUp = 24;
    public const int VolumeDown = 25;
    public const int Power = 26;
    public const int A = 29;
    public const int Tab = 61;
    public const int Space = 62;
    public const int Enter = 66;
    public const int Del = 67;
    public const int Menu = 82;
    public const int PageUp = 92;
    public const int PageDown = 93;
    public const int Escape = 111;
    public const int ForwardDel = 112;
    public const int MoveHome = 122;
    public const int MoveEnd = 123;
    public const int AppSwitch = 187;

    public const int MetaShiftOn = 0x1;
    public const int MetaAltOn = 0x2;
    public const int MetaCtrlOn = 0x1000;
}

/// <summary>
/// Serializes client â†’ device messages of the scrcpy control protocol (v5.x).
/// All integers are big-endian. See ControlMessageReader.java in the scrcpy server.
/// </summary>
public static class ControlMessages
{
    private const byte TypeInjectKeycode = 0;
    private const byte TypeInjectText = 1;
    private const byte TypeInjectTouch = 2;
    private const byte TypeInjectScroll = 3;
    private const byte TypeBackOrScreenOn = 4;
    private const byte TypeExpandNotificationPanel = 5;
    private const byte TypeExpandSettingsPanel = 6;
    private const byte TypeCollapsePanels = 7;
    private const byte TypeSetClipboard = 9;
    private const byte TypeSetDisplayPower = 10;
    private const byte TypeRotateDevice = 11;

    /// <summary>Touch from a generic finger (left mouse button behaves as a finger).</summary>
    public const long PointerIdGenericFinger = -2;

    private const int InjectTextMaxBytes = 300;

    public static byte[] InjectKeycode(int action, int keycode, int repeat = 0, int metaState = 0)
    {
        var b = new byte[14];
        b[0] = TypeInjectKeycode;
        b[1] = (byte)action;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(2), keycode);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(6), repeat);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(10), metaState);
        return b;
    }

    public static byte[] InjectText(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        int len = Math.Min(utf8.Length, InjectTextMaxBytes);
        var b = new byte[5 + len];
        b[0] = TypeInjectText;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(1), len);
        Array.Copy(utf8, 0, b, 5, len);
        return b;
    }

    public static byte[] InjectTouch(int action, long pointerId, int x, int y, int screenWidth, int screenHeight,
        float pressure, int actionButton = 0, int buttons = 0)
    {
        var b = new byte[32];
        b[0] = TypeInjectTouch;
        b[1] = (byte)action;
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(2), pointerId);
        WritePosition(b.AsSpan(10), x, y, screenWidth, screenHeight);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), ToU16FixedPoint(pressure));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(24), actionButton);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(28), buttons);
        return b;
    }

    /// <summary>Scroll amounts are in "notches" in [-16, 16].</summary>
    public static byte[] InjectScroll(int x, int y, int screenWidth, int screenHeight, float hScroll, float vScroll)
    {
        var b = new byte[21];
        b[0] = TypeInjectScroll;
        WritePosition(b.AsSpan(1), x, y, screenWidth, screenHeight);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(13), ToI16FixedPoint(hScroll / 16f));
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(15), ToI16FixedPoint(vScroll / 16f));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(17), 0);
        return b;
    }

    /// <summary>Presses BACK, or turns the screen on if it is off.</summary>
    public static byte[] BackOrScreenOn(int action) => new[] { TypeBackOrScreenOn, (byte)action };

    public static byte[] ExpandNotificationPanel() => new[] { TypeExpandNotificationPanel };
    public static byte[] ExpandSettingsPanel() => new[] { TypeExpandSettingsPanel };
    public static byte[] CollapsePanels() => new[] { TypeCollapsePanels };
    public static byte[] RotateDevice() => new[] { TypeRotateDevice };
    public static byte[] SetDisplayPower(bool on) => new[] { TypeSetDisplayPower, (byte)(on ? 1 : 0) };

    public static byte[] SetClipboard(string text, bool paste, long sequence = 0)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        var b = new byte[14 + utf8.Length];
        b[0] = TypeSetClipboard;
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(1), sequence);
        b[9] = (byte)(paste ? 1 : 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(10), utf8.Length);
        utf8.CopyTo(b, 14);
        return b;
    }

    private static void WritePosition(Span<byte> dst, int x, int y, int screenWidth, int screenHeight)
    {
        BinaryPrimitives.WriteInt32BigEndian(dst, x);
        BinaryPrimitives.WriteInt32BigEndian(dst[4..], y);
        BinaryPrimitives.WriteUInt16BigEndian(dst[8..], (ushort)screenWidth);
        BinaryPrimitives.WriteUInt16BigEndian(dst[10..], (ushort)screenHeight);
    }

    private static ushort ToU16FixedPoint(float f)
    {
        f = Math.Clamp(f, 0f, 1f);
        uint u = (uint)(f * 65536f);
        return (ushort)Math.Min(u, 0xFFFF);
    }

    private static short ToI16FixedPoint(float f)
    {
        f = Math.Clamp(f, -1f, 1f);
        int i = (int)(f * 32768f);
        return (short)Math.Clamp(i, short.MinValue, short.MaxValue);
    }
}
