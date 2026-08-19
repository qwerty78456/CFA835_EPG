namespace Cfa835SystemMonitor;

public enum CfaKey : byte
{
    Up = 1,
    Down = 2,
    Left = 3,
    Right = 4,
    Enter = 5,
    Exit = 6
}

public sealed record TemperatureReading(
    string Hardware,
    string Name,
    double? Celsius,
    bool IsCpu)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Hardware) ? Name : $"{Hardware} {Name}";
}

public sealed record InterfaceReading(
    ulong Luid,
    string Alias,
    string Description,
    ulong InOctets,
    ulong OutOctets);

public sealed record MetricSnapshot(
    DateTimeOffset Timestamp,
    double CpuPercent,
    IReadOnlyList<TemperatureReading> Temperatures,
    double? HottestCpuC,
    double ReceiveMbps,
    double TransmitMbps,
    bool DiskActive,
    bool NetworkReceiveActive,
    bool NetworkTransmitActive)
{
    public static MetricSnapshot Empty(DateTimeOffset timestamp) =>
        new(timestamp, 0, Array.Empty<TemperatureReading>(), null, 0, 0, false, false, false);
}

public interface IMetricSource : IDisposable
{
    MetricSnapshot Sample(DateTimeOffset now);
    IReadOnlyList<InterfaceReading> GetInterfaces();
    bool IsPawnIoInstalled { get; }
}
