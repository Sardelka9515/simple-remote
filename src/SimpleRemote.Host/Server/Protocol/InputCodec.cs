using System.Buffers.Binary;
using SimpleRemote.Input;

namespace SimpleRemote.Server.Protocol;

public enum DecodeStatus
{
    /// <summary>Whole buffer consumed cleanly.</summary>
    Ok,

    /// <summary>Unknown opcode or a truncated trailing message. Events decoded before the fault are still valid.</summary>
    Malformed,

    /// <summary>Buffer held more messages than the output spans could take. The remainder was dropped.</summary>
    Overflow,
}

/// <summary>
/// Decodes the binary input stream.
///
/// A single WebSocket message may carry several concatenated events, which is what lets the client
/// coalesce a frame worth of pointer motion and the server turn it into one batched SendInput call.
///
/// Every failure is reported rather than thrown: a malformed frame is a reason to ignore a message,
/// not to tear down a working connection.
/// </summary>
public static class InputCodec
{
    public static DecodeStatus Decode(
        ReadOnlySpan<byte> data,
        Span<InputEvent> events,
        Span<uint> pings,
        out int eventCount,
        out int pingCount)
    {
        eventCount = 0;
        pingCount = 0;

        var pos = 0;
        while (pos < data.Length)
        {
            var opcode = data[pos];
            var size = Opcodes.SizeOf(opcode);

            if (size < 0) return DecodeStatus.Malformed;
            if (pos + size > data.Length) return DecodeStatus.Malformed;

            var body = data.Slice(pos + 1, size - 1);

            switch (opcode)
            {
                case Opcodes.MouseMove:
                    if (eventCount >= events.Length) return DecodeStatus.Overflow;
                    events[eventCount++] = InputEvent.Move(ReadI16(body), ReadI16(body[2..]));
                    break;

                case Opcodes.MouseButton:
                    if (eventCount >= events.Length) return DecodeStatus.Overflow;
                    if (body[0] > (byte)MouseButton.Middle) return DecodeStatus.Malformed;
                    events[eventCount++] = InputEvent.Button((MouseButton)body[0], body[1] != 0);
                    break;

                case Opcodes.Scroll:
                    if (eventCount >= events.Length) return DecodeStatus.Overflow;
                    events[eventCount++] = InputEvent.Scroll(ReadI16(body), ReadI16(body[2..]));
                    break;

                case Opcodes.Key:
                    if (eventCount >= events.Length) return DecodeStatus.Overflow;
                    events[eventCount++] = InputEvent.Key(BinaryPrimitives.ReadUInt16LittleEndian(body), body[2] != 0);
                    break;

                case Opcodes.Ping:
                    if (pingCount >= pings.Length) return DecodeStatus.Overflow;
                    pings[pingCount++] = BinaryPrimitives.ReadUInt32LittleEndian(body);
                    break;

                default:
                    return DecodeStatus.Malformed;
            }

            pos += size;
        }

        return DecodeStatus.Ok;
    }

    /// <summary>Writes a pong into <paramref name="destination"/>, which must hold at least 5 bytes.</summary>
    public static int WritePong(Span<byte> destination, uint seq)
    {
        destination[0] = Opcodes.Pong;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[1..], seq);
        return 5;
    }

    /// <summary>Encodes a single event. Used by tests and by any future server-driven input.</summary>
    public static int Write(Span<byte> destination, in InputEvent e)
    {
        switch (e.Kind)
        {
            case InputKind.MouseMove:
                destination[0] = Opcodes.MouseMove;
                WriteI16(destination[1..], e.A);
                WriteI16(destination[3..], e.B);
                return 5;

            case InputKind.MouseButton:
                destination[0] = Opcodes.MouseButton;
                destination[1] = (byte)e.A;
                destination[2] = (byte)(e.B != 0 ? 1 : 0);
                return 3;

            case InputKind.Scroll:
                destination[0] = Opcodes.Scroll;
                WriteI16(destination[1..], e.A);
                WriteI16(destination[3..], e.B);
                return 5;

            case InputKind.Key:
                destination[0] = Opcodes.Key;
                BinaryPrimitives.WriteUInt16LittleEndian(destination[1..], (ushort)e.A);
                destination[3] = (byte)(e.B != 0 ? 1 : 0);
                return 4;

            default:
                return 0;
        }
    }

    private static short ReadI16(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadInt16LittleEndian(span);

    private static void WriteI16(Span<byte> span, int value) =>
        BinaryPrimitives.WriteInt16LittleEndian(span, (short)Math.Clamp(value, short.MinValue, short.MaxValue));
}
