namespace Cfa835SystemMonitor.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void ClearCommandMatchesDatasheetVector()
    {
        byte[] encoded = CfaPacket.Command(0x06).Encode();
        Assert.Equal(new byte[] { 0x06, 0x00, 0x97, 0x5B }, encoded);
    }

    [Fact]
    public void ParserHandlesFragmentedAndConcatenatedPackets()
    {
        CfaPacketParser parser = new();
        byte[] first = new CfaPacket(0x41, [1, 2, 3]).Encode();
        byte[] second = new CfaPacket(0x80, [5]).Encode();

        Assert.Empty(parser.Feed(first.AsSpan(0, 2)));
        byte[] remainder = first.AsSpan(2).ToArray().Concat(second).ToArray();
        IReadOnlyList<CfaPacket> packets = parser.Feed(remainder);

        Assert.Equal(2, packets.Count);
        Assert.Equal((byte)0x41, packets[0].Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, packets[0].Data);
        Assert.Equal((byte)0x80, packets[1].Type);
    }

    [Fact]
    public void ParserResynchronizesAfterNoiseAndBadCrc()
    {
        CfaPacketParser parser = new();
        byte[] bad = CfaPacket.Command(0x06).Encode();
        bad[^1] ^= 0x40;
        byte[] valid = new CfaPacket(0x46, []).Encode();
        byte[] stream = new byte[] { 0xFF, 0xAA }.Concat(bad).Concat(valid).ToArray();

        IReadOnlyList<CfaPacket> packets = parser.Feed(stream);

        CfaPacket packet = Assert.Single(packets);
        Assert.Equal((byte)0x46, packet.Type);
    }

    [Fact]
    public async Task DeviceUsesDocumentedTextAndLedMappings()
    {
        FakeTransport transport = new();
        Cfa835Device device = new(transport, Microsoft.Extensions.Logging.Abstractions.NullLogger<Cfa835Device>.Instance);
        await device.OpenAsync("COM3", true, CancellationToken.None);
        await device.WriteRowAsync(2, "hello", CancellationToken.None);
        await device.SetLedAsync(1, 100, 100, CancellationToken.None);

        Assert.Contains(transport.Commands, item => item.Command == 0x17 && item.Data.SequenceEqual(new byte[] { 0x3F, 0 }));
        Assert.Contains(transport.Commands, item => item.Command == 0x1F && item.Data[0] == 0 && item.Data[1] == 2 && item.Data.Length == 22);
        Assert.Contains(transport.Commands, item => item.Command == 0x22 && item.Data.SequenceEqual(new byte[] { 9, 100 }));
        Assert.Contains(transport.Commands, item => item.Command == 0x22 && item.Data.SequenceEqual(new byte[] { 10, 100 }));
        await device.DisposeAsync();
    }

    private sealed class FakeTransport : ICfaTransport
    {
        public List<(byte Command, byte[] Data)> Commands { get; } = [];
        public bool IsOpen { get; private set; }
        public event Action<CfaPacket>? ReportReceived;
        public event Action<Exception?>? ConnectionLost;

        public Task OpenAsync(string portName, CancellationToken cancellationToken)
        {
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task<CfaPacket> SendCommandAsync(byte command, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Commands.Add((command, data.ToArray()));
            byte[] response = command == 0x01 ? "CFA835:h2.0,f1.7"u8.ToArray() : [];
            return Task.FromResult(new CfaPacket((byte)(0x40 | command), response));
        }

        public Task CloseAsync()
        {
            IsOpen = false;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync() => await CloseAsync();
    }
}
