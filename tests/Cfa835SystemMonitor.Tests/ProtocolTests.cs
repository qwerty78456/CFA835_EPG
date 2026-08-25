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

    [Fact]
    public async Task GraphicCommandsMatchTheDatasheetSubcommands()
    {
        FakeTransport transport = new();
        await using Cfa835Device device = new(transport, Microsoft.Extensions.Logging.Abstractions.NullLogger<Cfa835Device>.Instance);
        await device.OpenAsync("COM9", enableKeyReports: false, CancellationToken.None);

        await device.ClearDisplayAsync(CancellationToken.None);
        await device.SetGraphicOptionsAsync(manualFlush: true, gammaCorrection: true, CancellationToken.None);
        await device.FlushBufferAsync(CancellationToken.None);
        await device.DrawRectangleAsync(4, 5, 10, 6, 248, 0, CancellationToken.None);
        byte shade = await device.ReadPixelAsync(12, 13, CancellationToken.None);

        Assert.Contains(transport.Commands, item => item.Command == 0x06 && item.Data.Length == 0);
        Assert.Contains(transport.Commands, item => item.Command == 0x28 && item.Data.SequenceEqual(new byte[] { 0, 0x03 }));
        Assert.Contains(transport.Commands, item => item.Command == 0x28 && item.Data.SequenceEqual(new byte[] { 1 }));
        Assert.Contains(
            transport.Commands,
            item => item.Command == 0x28 && item.Data.SequenceEqual(new byte[] { 7, 4, 5, 10, 6, 248, 0 }));
        Assert.Contains(transport.Commands, item => item.Command == 0x28 && item.Data.SequenceEqual(new byte[] { 5, 12, 13 }));
        Assert.Equal(0x40, shade);
    }

    [Fact]
    public async Task SendImageStreamsPixelsBehindASubcommandTwoHeader()
    {
        FakeTransport transport = new();
        await using Cfa835Device device = new(transport, Microsoft.Extensions.Logging.Abstractions.NullLogger<Cfa835Device>.Instance);
        await device.OpenAsync("COM9", enableKeyReports: false, CancellationToken.None);

        byte[] pixels = new byte[8 * 3];
        Array.Fill(pixels, (byte)0x88);
        await device.SendImageAsync(20, 30, 8, 3, pixels, transparency: false, invert: false, CancellationToken.None);

        (byte Command, byte[] Header, byte[] Payload) sent = Assert.Single(transport.Streams);
        Assert.Equal(0x28, sent.Command);
        Assert.Equal(new byte[] { 2, 0, 20, 30, 8, 3 }, sent.Header);
        Assert.Equal(pixels, sent.Payload);
    }

    [Theory]
    [InlineData(240, 0, 8, 4)]
    [InlineData(0, 66, 4, 4)]
    [InlineData(-1, 0, 4, 4)]
    public async Task SendImageRejectsRectanglesThatLeaveTheDisplay(int x, int y, int width, int height)
    {
        FakeTransport transport = new();
        await using Cfa835Device device = new(transport, Microsoft.Extensions.Logging.Abstractions.NullLogger<Cfa835Device>.Instance);
        await device.OpenAsync("COM9", enableKeyReports: false, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.SendImageAsync(
            x, y, width, height, new byte[width * height], false, false, CancellationToken.None));
        Assert.Empty(transport.Streams);
    }

    private sealed class FakeTransport : ICfaTransport
    {
        public List<(byte Command, byte[] Data)> Commands { get; } = [];
        public List<(byte Command, byte[] Header, byte[] Payload)> Streams { get; } = [];
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
            byte[] payload = data.ToArray();
            Commands.Add((command, payload));
            byte[] response = command switch
            {
                0x01 => "CFA835:h2.0,f1.7"u8.ToArray(),
                // Command 40 subcommand 5 read form echoes the subcommand plus the sampled shade.
                0x28 when payload.Length > 0 && payload[0] == 5 => [5, 0x40],
                _ => []
            };

            return Task.FromResult(new CfaPacket((byte)(0x40 | command), response));
        }

        public Task<CfaPacket> SendStreamingCommandAsync(
            byte command,
            ReadOnlyMemory<byte> header,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            Streams.Add((command, header.ToArray(), payload.ToArray()));
            byte[] response = header.Length > 0 ? [header.Span[0]] : [];
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
