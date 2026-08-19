namespace Cfa835SystemMonitor;

public sealed record CfaPacket(byte Type, byte[] Data)
{
    public byte CommandCode => (byte)(Type & 0x3F);
    public byte PacketClass => (byte)(Type & 0xC0);

    public byte[] Encode()
    {
        if (Data.Length > byte.MaxValue)
        {
            throw new InvalidOperationException("CFA packet payload is too large.");
        }

        byte[] packet = new byte[Data.Length + 4];
        packet[0] = Type;
        packet[1] = (byte)Data.Length;
        Data.CopyTo(packet, 2);
        ushort crc = ComputeCrc(packet.AsSpan(0, Data.Length + 2));
        packet[^2] = (byte)(crc & 0xFF);
        packet[^1] = (byte)(crc >> 8);
        return packet;
    }

    public static CfaPacket Command(byte command, params byte[] data) =>
        new((byte)(command & 0x3F), data);

    public static ushort ComputeCrc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (byte value in bytes)
        {
            byte current = value;
            for (int bit = 0; bit < 8; bit++)
            {
                if (((crc ^ current) & 0x01) != 0)
                {
                    crc = (ushort)((crc >> 1) ^ 0x8408);
                }
                else
                {
                    crc >>= 1;
                }

                current >>= 1;
            }
        }

        return (ushort)~crc;
    }
}

public sealed class CfaPacketParser
{
    private readonly List<byte> _buffer = new(512);

    public IReadOnlyList<CfaPacket> Feed(ReadOnlySpan<byte> bytes)
    {
        for (int index = 0; index < bytes.Length; index++)
        {
            _buffer.Add(bytes[index]);
        }

        List<CfaPacket> packets = [];
        while (_buffer.Count >= 4)
        {
            int payloadLength = _buffer[1];
            int packetLength = payloadLength + 4;
            if (_buffer.Count < packetLength)
            {
                int laterPacket = FindCompletePacketAfterStart();
                if (laterPacket > 0)
                {
                    _buffer.RemoveRange(0, laterPacket);
                    continue;
                }

                break;
            }

            byte[] candidate = _buffer.GetRange(0, packetLength).ToArray();
            ushort expected = (ushort)(candidate[^2] | (candidate[^1] << 8));
            ushort actual = CfaPacket.ComputeCrc(candidate.AsSpan(0, packetLength - 2));
            if (actual == expected)
            {
                byte[] data = candidate.AsSpan(2, payloadLength).ToArray();
                packets.Add(new CfaPacket(candidate[0], data));
                _buffer.RemoveRange(0, packetLength);
            }
            else
            {
                _buffer.RemoveAt(0);
            }
        }

        return packets;
    }

    private int FindCompletePacketAfterStart()
    {
        for (int offset = 1; offset <= _buffer.Count - 4; offset++)
        {
            int length = _buffer[offset + 1] + 4;
            if (offset + length > _buffer.Count)
            {
                continue;
            }

            byte[] candidate = _buffer.GetRange(offset, length).ToArray();
            ushort expected = (ushort)(candidate[^2] | (candidate[^1] << 8));
            ushort actual = CfaPacket.ComputeCrc(candidate.AsSpan(0, length - 2));
            if (actual == expected)
            {
                return offset;
            }
        }

        return -1;
    }

    public void Reset() => _buffer.Clear();
}

public sealed class CfaCommandException : IOException
{
    public CfaCommandException(byte command, CfaPacket errorPacket)
        : base($"CFA835 rejected command 0x{command:X2}; error payload: {Convert.ToHexString(errorPacket.Data)}")
    {
        Command = command;
        ErrorPacket = errorPacket;
    }

    public byte Command { get; }
    public CfaPacket ErrorPacket { get; }
}
