using System.Buffers.Binary;
using Seamdesk.Core;

namespace Seamdesk.Transport;

/// <summary>Everything that crosses the wire. Data only: the Windows host and the UI give the input frames meaning.</summary>
public abstract record Frame;

/// <summary>First frame on every connection.</summary>
public sealed record Hello(int Protocol, string AppVersion, string MachineId) : Frame;

/// <summary>The sender will not talk to the receiver. <c>TheirVersion</c> is the sender's app version, for the receiver's tray.</summary>
public sealed record Refuse(string Reason, string TheirVersion) : Frame;

/// <summary>"I accepted your Hello." Both sides are Ready once they have sent and received one.</summary>
public sealed record ReadyFrame : Frame;

public sealed record Ping : Frame;

/// <summary>One J-PAKE message (round 1, 2 or 3), as BouncyCastle's payload fields.</summary>
public sealed record Pake(int Round, string Participant, IReadOnlyList<byte[]> Values) : Frame;

public sealed record PointerMove(int Dx, int Dy) : Frame;

public enum MouseButton : byte { Left, Right, Middle, X1, X2 }

public sealed record PointerButton(MouseButton Button, bool Down) : Frame;

public sealed record Wheel(int Delta, bool Horizontal) : Frame;

public sealed record Key(ushort VirtualKey, ushort ScanCode, bool Down, bool Extended) : Frame;

/// <summary>The cursor now belongs to <c>Machine</c> at <c>Point</c>. Also used to hand it back to the controller.</summary>
public sealed record Handoff(string Machine, Point Point) : Frame;

/// <summary>The sender went to (or left) the secure desktop; crossing into it is refused while locked.</summary>
public sealed record Locked(bool IsLocked) : Frame;

/// <summary>The sender's monitors in its own desktop coordinates.</summary>
public sealed record Monitors(IReadOnlyList<Rect> Rects) : Frame;

/// <summary>Where every machine sits on the desk. The highest revision wins.</summary>
public sealed record Layout(long Revision, IReadOnlyList<MachineOffset> Offsets) : Frame;

public sealed record MachineOffset(string Machine, Point Offset);

/// <summary>Clipboard text, sent just before a <see cref="Handoff"/> into the peer. The receiver decides whether to take it.</summary>
public sealed record ClipboardText(string Text) : Frame;

public sealed class ProtocolException(string message) : Exception(message);

/// <summary>Frames on the wire: a 4-byte big-endian length, a type byte, then the fields.</summary>
public static class FrameCodec
{
    public const int MaxLength = 64 * 1024;

    private enum Type : byte { Hello = 1, Refuse, Ready, Ping, Pake, PointerMove, PointerButton, Wheel, Key, Handoff, Locked, Monitors, Layout, ClipboardText }

    /// <summary>A frame with no wire type, or longer than <see cref="MaxLength"/>, throws ArgumentException before a byte is written.</summary>
    public static async Task WriteAsync(Stream stream, Frame frame, CancellationToken ct = default)
    {
        using MemoryStream body = new();
        body.Write([0, 0, 0, 0]);
        using (BinaryWriter w = new(body, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            Encode(w, frame);
        }

        byte[] bytes = body.ToArray();
        if (bytes.Length - 4 > MaxLength)
        {
            throw new ArgumentException($"{frame.GetType().Name} is {bytes.Length - 4} bytes, over {MaxLength}");
        }

        BinaryPrimitives.WriteInt32BigEndian(bytes, bytes.Length - 4);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Null at a clean end of stream.</summary>
    public static async Task<Frame?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        if (!await ReadExactly(stream, header, ct))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > MaxLength)
        {
            throw new ProtocolException($"frame length {length}");
        }

        byte[] body = new byte[length];
        if (!await ReadExactly(stream, body, ct))
        {
            throw new EndOfStreamException();
        }

        using BinaryReader r = new(new MemoryStream(body));
        try
        {
            return Decode(r);
        }
        catch (EndOfStreamException)
        {
            throw new ProtocolException("truncated frame");
        }
    }

    private static async Task<bool> ReadExactly(Stream stream, byte[] buffer, CancellationToken ct) =>
        await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct) == buffer.Length;

    private static void Encode(BinaryWriter w, Frame frame)
    {
        switch (frame)
        {
            case Hello f: w.Write((byte)Type.Hello); w.Write(f.Protocol); w.Write(f.AppVersion); w.Write(f.MachineId); break;
            case Refuse f: w.Write((byte)Type.Refuse); w.Write(f.Reason); w.Write(f.TheirVersion); break;
            case ReadyFrame: w.Write((byte)Type.Ready); break;
            case Ping: w.Write((byte)Type.Ping); break;
            case Pake f:
                w.Write((byte)Type.Pake); w.Write(f.Round); w.Write(f.Participant); w.Write(f.Values.Count);
                foreach (byte[] v in f.Values)
                {
                    w.Write(v.Length);
                    w.Write(v);
                }

                break;
            case PointerMove f: w.Write((byte)Type.PointerMove); w.Write(f.Dx); w.Write(f.Dy); break;
            case PointerButton f: w.Write((byte)Type.PointerButton); w.Write((byte)f.Button); w.Write(f.Down); break;
            case Wheel f: w.Write((byte)Type.Wheel); w.Write(f.Delta); w.Write(f.Horizontal); break;
            case Key f: w.Write((byte)Type.Key); w.Write(f.VirtualKey); w.Write(f.ScanCode); w.Write(f.Down); w.Write(f.Extended); break;
            case Handoff f: w.Write((byte)Type.Handoff); w.Write(f.Machine); w.Write(f.Point.X); w.Write(f.Point.Y); break;
            case Locked f: w.Write((byte)Type.Locked); w.Write(f.IsLocked); break;
            case Monitors f:
                w.Write((byte)Type.Monitors); w.Write(f.Rects.Count);
                foreach (Rect m in f.Rects)
                {
                    w.Write(m.Left); w.Write(m.Top); w.Write(m.Width); w.Write(m.Height);
                }

                break;
            case Layout f:
                w.Write((byte)Type.Layout); w.Write(f.Revision); w.Write(f.Offsets.Count);
                foreach (MachineOffset o in f.Offsets)
                {
                    w.Write(o.Machine); w.Write(o.Offset.X); w.Write(o.Offset.Y);
                }

                break;
            case ClipboardText f: w.Write((byte)Type.ClipboardText); w.Write(f.Text); break;
            default: throw new ArgumentException($"unknown frame {frame}");
        }
    }

    private static Frame Decode(BinaryReader r)
    {
        Type type = (Type)r.ReadByte();
        Frame frame = type switch
        {
            Type.Hello => new Hello(r.ReadInt32(), r.ReadString(), r.ReadString()),
            Type.Refuse => new Refuse(r.ReadString(), r.ReadString()),
            Type.Ready => new ReadyFrame(),
            Type.Ping => new Ping(),
            Type.Pake => new Pake(r.ReadInt32(), r.ReadString(), List(r, () => r.ReadBytes(Count(r)))),
            Type.PointerMove => new PointerMove(r.ReadInt32(), r.ReadInt32()),
            Type.PointerButton => new PointerButton((MouseButton)r.ReadByte(), r.ReadBoolean()),
            Type.Wheel => new Wheel(r.ReadInt32(), r.ReadBoolean()),
            Type.Key => new Key(r.ReadUInt16(), r.ReadUInt16(), r.ReadBoolean(), r.ReadBoolean()),
            Type.Handoff => new Handoff(r.ReadString(), new Point(r.ReadInt32(), r.ReadInt32())),
            Type.Locked => new Locked(r.ReadBoolean()),
            Type.Monitors => new Monitors(List(r, () => new Rect(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()))),
            Type.Layout => new Layout(r.ReadInt64(), List(r, () => new MachineOffset(r.ReadString(), new Point(r.ReadInt32(), r.ReadInt32())))),
            Type.ClipboardText => new ClipboardText(r.ReadString()),
            _ => throw new ProtocolException($"unknown frame type {(byte)type}"),
        };

        if (r.BaseStream.Position != r.BaseStream.Length)
        {
            throw new ProtocolException($"trailing bytes after {type}");
        }

        return frame;
    }

    private static int Count(BinaryReader r)
    {
        int n = r.ReadInt32();
        return n >= 0 && n <= r.BaseStream.Length - r.BaseStream.Position ? n : throw new ProtocolException($"count {n}");
    }

    private static List<T> List<T>(BinaryReader r, Func<T> item) => [.. Enumerable.Range(0, Count(r)).Select(_ => item())];
}
