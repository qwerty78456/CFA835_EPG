using Microsoft.Extensions.Logging.Abstractions;

namespace Cfa835SystemMonitor.Tests;

public sealed class DeviceLocatorTests
{
    private static DeviceOptions Options(string serial = "SERIAL-A", string fallback = "COM3", bool probe = true) =>
        new() { Vid = "223B", Pid = "0005", Serial = serial, FallbackPort = fallback, ProbeAllPorts = probe };

    [Fact]
    public void ConfiguredSerialIsPreferredWhenItIsPresent()
    {
        IReadOnlyList<PortCandidate> candidates = CfaDeviceLocator.BuildCandidates(
            Options(),
            [new UsbPortEntry("SERIAL-B", "COM7", true), new UsbPortEntry("SERIAL-A", "COM5", true)],
            ["COM3", "COM5", "COM7"]);

        Assert.Equal("COM5", candidates[0].Port);
        Assert.Contains("configured serial SERIAL-A", candidates[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherUnitOutranksTheFallbackPort()
    {
        // The deployment failure this guards: a configuration carrying the workshop unit's serial
        // reached a site whose module has a different one. The old rule fell straight through to
        // device.fallbackPort, which was an unrelated device that opened fine and never answered.
        IReadOnlyList<PortCandidate> candidates = CfaDeviceLocator.BuildCandidates(
            Options(serial: "WORKSHOP-UNIT"),
            [new UsbPortEntry("SITE-UNIT", "COM7", true)],
            ["COM3", "COM7"]);

        Assert.Equal("COM7", candidates[0].Port);
        Assert.Contains("serial SITE-UNIT", candidates[0].Reason, StringComparison.Ordinal);
        Assert.Equal("COM3", candidates[1].Port);
        Assert.Contains("fallback", candidates[1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PortsThatAreNotPresentAreNeverOffered()
    {
        IReadOnlyList<PortCandidate> candidates = CfaDeviceLocator.BuildCandidates(
            Options(),
            [new UsbPortEntry("SERIAL-A", "COM9", false)],
            ["COM3"]);

        Assert.Equal("COM3", Assert.Single(candidates).Port);
    }

    [Fact]
    public void EveryRemainingPortIsProbedLast()
    {
        IReadOnlyList<PortCandidate> candidates = CfaDeviceLocator.BuildCandidates(
            Options(),
            [],
            ["COM1", "COM3", "COM4"]);

        Assert.Equal(["COM3", "COM1", "COM4"], candidates.Select(item => item.Port));
        Assert.Contains("probe", candidates[1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbingCanBeDisabledForMachinesWithOtherSerialDevices()
    {
        IReadOnlyList<PortCandidate> candidates = CfaDeviceLocator.BuildCandidates(
            Options(probe: false),
            [],
            ["COM1", "COM3", "COM4"]);

        Assert.Equal("COM3", Assert.Single(candidates).Port);
    }

    [Fact]
    public void CandidatesAreNeverDuplicated()
    {
        IReadOnlyList<PortCandidate> candidates = CfaDeviceLocator.BuildCandidates(
            Options(fallback: "COM5"),
            [new UsbPortEntry("SERIAL-A", "COM5", true)],
            ["COM5"]);

        Assert.Equal("COM5", Assert.Single(candidates).Port);
    }

    [Fact]
    public async Task ResolveSkipsPortsThatOpenButNeverAnswer()
    {
        FakeProbe probe = new();
        probe.Responses["COM3"] = null;                   // opens, stays silent: the prod symptom
        probe.Responses["COM7"] = "CFA835:h2.0,f1.6";

        string port = await CfaDeviceLocator.ProbeCandidatesAsync(
            [new PortCandidate("COM3", "configured device.fallbackPort"), new PortCandidate("COM7", "probe")],
            () => probe.Create(),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal("COM7", port);
        Assert.Equal(["COM3", "COM7"], probe.Attempted);
    }

    [Fact]
    public async Task ResolveThrowsWhenNothingAnswersAndNamesWhatWasTried()
    {
        FakeProbe probe = new();

        FileNotFoundException error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            CfaDeviceLocator.ProbeCandidatesAsync(
                [new PortCandidate("COM3", "configured device.fallbackPort")],
                () => probe.Create(),
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Contains("No CFA835 answered", error.Message, StringComparison.Ordinal);
        Assert.Contains("COM3 (configured device.fallbackPort)", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Transport stand-in whose per-port answers are scripted; records the probe order.</summary>
    private sealed class FakeProbe
    {
        public Dictionary<string, string?> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Attempted { get; } = [];

        public ICfaTransport Create() => new Transport(this);

        private sealed class Transport(FakeProbe owner) : ICfaTransport
        {
            private string? _port;

            public bool IsOpen { get; private set; }
            public event Action<CfaPacket>? ReportReceived;
            public event Action<Exception?>? ConnectionLost;

            public Task OpenAsync(string portName, CancellationToken cancellationToken)
            {
                _ = ReportReceived;
                _ = ConnectionLost;
                _port = portName;
                owner.Attempted.Add(portName);
                IsOpen = true;
                return Task.CompletedTask;
            }

            public Task<CfaPacket> SendCommandAsync(
                byte command, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
            {
                if (_port is null || !owner.Responses.TryGetValue(_port, out string? version) || version is null)
                {
                    return Task.FromException<CfaPacket>(new TimeoutException($"No device on {_port}."));
                }

                return Task.FromResult(new CfaPacket(
                    (byte)(0x40 | command), System.Text.Encoding.ASCII.GetBytes(version)));
            }

            public Task<CfaPacket> SendStreamingCommandAsync(
                byte command,
                ReadOnlyMemory<byte> header,
                ReadOnlyMemory<byte> payload,
                CancellationToken cancellationToken) =>
                Task.FromException<CfaPacket>(new NotSupportedException());

            public Task CloseAsync()
            {
                IsOpen = false;
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync() => await CloseAsync();
        }
    }
}
