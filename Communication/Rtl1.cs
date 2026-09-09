namespace rt4k_pi;

// RTL1 binary framing, as documented in PROTOCOL.md ("Transport & Framing").
//
// Wire layout (little endian):
//   A5 5A | nonce(u16) | len(u16) | type(u8) | seq(u8) | payload[len] | crc16(u16)
//
// The CRC is CRC-16/CCITT-FALSE over bytes [2 .. 7+len], i.e. everything except
// the two sync bytes and the CRC itself.

public enum Rtl1Type : byte
{
    Cmd = 1,
    Resp = 2,
    Data = 3,
    Ack = 4,
    Nak = 5,
    Abort = 6,
    Ping = 7
}

public enum Rtl1Nak : byte
{
    Crc = 1,
    Nonce = 2,
    Seq = 3,
    Len = 4,
    Busy = 5,
    State = 6
}

public readonly struct Rtl1Frame(ushort nonce, Rtl1Type type, byte seq, byte[] payload)
{
    public ushort Nonce { get; } = nonce;
    public Rtl1Type Type { get; } = type;
    public byte Seq { get; } = seq;
    public byte[] Payload { get; } = payload;

    public override string ToString() => $"RTL1 {Type} nonce=0x{Nonce:X4} seq={Seq} len={Payload.Length}";
}

public static class Rtl1
{
    public const byte Sync0 = 0xA5;
    public const byte Sync1 = 0x5A;
    public const int Overhead = 10;
    public const int MaxPayload = 2048;
    public const int MaxFrame = MaxPayload + Overhead;

    // CRC-16/CCITT-FALSE: poly 0x1021, init 0xFFFF, no reflection, xorout 0x0000.
    // Self-test: Crc16("123456789") == 0x29B1
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;

        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);

            for (int i = 0; i < 8; i++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
        }

        return crc;
    }

    public static byte[] Encode(ushort nonce, Rtl1Type type, byte seq, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentException($"RTL1 payload of {payload.Length} bytes exceeds the {MaxPayload} byte maximum", nameof(payload));
        }

        byte[] frame = new byte[Overhead + payload.Length];

        frame[0] = Sync0;
        frame[1] = Sync1;
        frame[2] = (byte)(nonce & 0xFF);
        frame[3] = (byte)(nonce >> 8);
        frame[4] = (byte)(payload.Length & 0xFF);
        frame[5] = (byte)(payload.Length >> 8);
        frame[6] = (byte)type;
        frame[7] = seq;
        payload.CopyTo(frame.AsSpan(8));

        ushort crc = Crc16(frame.AsSpan(2, 6 + payload.Length));
        frame[8 + payload.Length] = (byte)(crc & 0xFF);
        frame[9 + payload.Length] = (byte)(crc >> 8);

        return frame;
    }

    public static byte[] Encode(ushort nonce, Rtl1Type type, byte seq) => Encode(nonce, type, seq, ReadOnlySpan<byte>.Empty);
}

// Byte-fed, resync-safe RTL1 decoder. Bytes that aren't part of a valid looking
// frame are handed back to the caller so the text plane keeps working while a
// binary session is open (the device prints "[COM] get done" and friends on the
// text plane once the frames have drained).
public class Rtl1Decoder(Action<Rtl1Frame> onFrame, Action<byte[]> onPassthrough)
{
    private enum State
    {
        Sync0,
        Sync1,
        Header,
        Payload,
        CrcLo,
        CrcHi
    }

    private State state = State.Sync0;
    private readonly byte[] header = new byte[6];
    private int headerCount;
    private byte[] payload = [];
    private int payloadCount;
    private ushort crc;
    private readonly List<byte> passthrough = [];

    public void Reset()
    {
        state = State.Sync0;
        headerCount = 0;
        payloadCount = 0;
        passthrough.Clear();
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            FeedByte(b);
        }

        FlushPassthrough();
    }

    private void FeedByte(byte b)
    {
        switch (state)
        {
            case State.Sync0:
                if (b == Rtl1.Sync0)
                {
                    state = State.Sync1;
                }
                else
                {
                    passthrough.Add(b);
                }
                break;

            case State.Sync1:
                if (b == Rtl1.Sync1)
                {
                    state = State.Header;
                    headerCount = 0;
                }
                else
                {
                    // False start: the sync byte we swallowed wasn't a frame after all
                    passthrough.Add(Rtl1.Sync0);
                    state = State.Sync0;
                    FeedByte(b);
                }
                break;

            case State.Header:
                header[headerCount++] = b;

                if (headerCount == header.Length)
                {
                    int len = header[2] | (header[3] << 8);

                    if (len > Rtl1.MaxPayload)
                    {
                        // Impossible length, this was never a frame. Resync.
                        Resync();
                        break;
                    }

                    payload = len > 0 ? new byte[len] : [];
                    payloadCount = 0;
                    state = len > 0 ? State.Payload : State.CrcLo;
                }
                break;

            case State.Payload:
                payload[payloadCount++] = b;

                if (payloadCount == payload.Length)
                {
                    state = State.CrcLo;
                }
                break;

            case State.CrcLo:
                crc = b;
                state = State.CrcHi;
                break;

            case State.CrcHi:
                crc |= (ushort)(b << 8);
                Complete();
                break;
        }
    }

    private void Complete()
    {
        byte[] check = new byte[6 + payload.Length];
        header.CopyTo(check, 0);
        payload.CopyTo(check, 6);

        if (Rtl1.Crc16(check) == crc)
        {
            ushort nonce = (ushort)(header[0] | (header[1] << 8));
            FlushPassthrough();
            onFrame(new Rtl1Frame(nonce, (Rtl1Type)header[4], header[5], payload));
            state = State.Sync0;
            headerCount = 0;
        }
        else
        {
            Resync();
        }
    }

    // Something that looked like a frame wasn't one. Replay the bytes we held
    // back through the passthrough so text isn't lost, and start scanning again.
    private void Resync()
    {
        passthrough.Add(Rtl1.Sync0);
        passthrough.Add(Rtl1.Sync1);

        for (int i = 0; i < headerCount; i++)
        {
            passthrough.Add(header[i]);
        }

        for (int i = 0; i < payloadCount; i++)
        {
            passthrough.Add(payload[i]);
        }

        state = State.Sync0;
        headerCount = 0;
        payloadCount = 0;
    }

    private void FlushPassthrough()
    {
        if (passthrough.Count > 0)
        {
            onPassthrough([.. passthrough]);
            passthrough.Clear();
        }
    }
}
