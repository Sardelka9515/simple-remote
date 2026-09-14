namespace SimpleRemote.Server.Protocol;

/// <summary>
/// Binary opcodes for the input hot path. WebSocket frame type (binary vs text) already separates
/// input from control traffic, so these never collide with the JSON messages.
/// </summary>
public static class Opcodes
{
    // Client -> server
    public const byte MouseMove = 0x01;   // i16 dx, i16 dy
    public const byte MouseButton = 0x02; // u8 button, u8 down
    public const byte Scroll = 0x03;      // i16 dx, i16 dy
    public const byte Key = 0x04;         // u16 vk, u8 down
    public const byte Ping = 0x05;        // u32 seq

    /// <summary>
    /// When the phone produced the motion that follows in the same message: u32 client time in
    /// tenths of a millisecond, wrapping. Lets the host replay motion at the pace it was made rather
    /// than the pace Wi-Fi happened to deliver it.
    /// </summary>
    public const byte FrameTime = 0x06;   // u32 time (0.1 ms)

    // Server -> client
    public const byte Pong = 0x81;        // u32 seq

    /// <summary>Total encoded size of a message including its opcode byte, or -1 if unknown.</summary>
    public static int SizeOf(byte opcode) => opcode switch
    {
        MouseMove => 5,
        MouseButton => 3,
        Scroll => 5,
        Key => 4,
        Ping => 5,
        FrameTime => 5,
        Pong => 5,
        _ => -1,
    };
}
