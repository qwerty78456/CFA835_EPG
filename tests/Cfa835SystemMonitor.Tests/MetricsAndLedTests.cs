namespace Cfa835SystemMonitor.Tests;

public sealed class MetricsAndLedTests
{
    [Fact]
    public void CpuUsageUsesIdleAndTotalDeltas()
    {
        double result = CpuUsageSampler.Calculate(
            previousIdle: 100,
            previousKernel: 300,
            previousUser: 200,
            currentIdle: 120,
            currentKernel: 360,
            currentUser: 240);

        Assert.Equal(80, result, 5);
    }

    [Fact]
    public void NetworkSamplerCalculatesDecimalMegabitsAndDirection()
    {
        FakeNetworkProvider provider = new();
        NetworkRateSampler sampler = new(provider);
        DateTimeOffset start = DateTimeOffset.Parse("2026-08-17T00:00:00Z");
        provider.Current = [new InterfaceReading(1, "Ethernet", "Physical", 1000, 2000)];
        _ = sampler.Sample(start);
        provider.Current = [new InterfaceReading(1, "Ethernet", "Physical", 126_000, 64_500)];

        NetworkRateSample sample = sampler.Sample(start.AddSeconds(1));

        Assert.Equal(1.0, sample.ReceiveMbps, 5);
        Assert.Equal(0.5, sample.TransmitMbps, 5);
        Assert.True(sample.ReceiveActive);
        Assert.True(sample.TransmitActive);
    }

    [Fact]
    public void NetworkSamplerIgnoresCounterResets()
    {
        FakeNetworkProvider provider = new();
        NetworkRateSampler sampler = new(provider);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        provider.Current = [new InterfaceReading(1, "Ethernet", "Physical", 1000, 2000)];
        _ = sampler.Sample(start);
        provider.Current = [new InterfaceReading(1, "Ethernet", "Physical", 10, 20)];

        NetworkRateSample sample = sampler.Sample(start.AddSeconds(1));

        Assert.Equal(0, sample.ReceiveMbps);
        Assert.Equal(0, sample.TransmitMbps);
        Assert.False(sample.ReceiveActive);
        Assert.False(sample.TransmitActive);
    }

    [Fact]
    public void HottestCpuExcludesDistanceToTjMaxSensors()
    {
        TemperatureReading[] readings =
        [
            new("Intel CPU", "CPU Package", 42, true),
            new("Intel CPU", "Core Max", 47, true),
            new("Intel CPU", "Core Distance to TjMax", 53, true),
            new("GPU", "GPU Core", 70, false)
        ];

        Assert.Equal(47, CpuTemperatureSelector.Hottest(readings));
    }

    [Fact]
    public void HottestCpuReturnsNullWhenOnlyDistanceOrUnavailableValuesExist()
    {
        TemperatureReading[] readings =
        [
            new("Intel CPU", "Core Distance to TjMax", 91, true),
            new("Intel CPU", "CPU Package", null, true)
        ];

        Assert.Null(CpuTemperatureSelector.Hottest(readings));
    }

    [Fact]
    public void TemperatureReadingsFallBackWhenPrimaryValuesAreUnavailable()
    {
        TemperatureReading[] primary =
        [
            new("Intel CPU", "CPU Package", null, true),
            new("Intel CPU", "Core Max", double.NaN, true)
        ];
        TemperatureReading[] fallback =
        [
            new("Windows ACPI", "TZ00", 31.5, false),
            new("Windows ACPI", "TZ01", 33.5, false)
        ];

        TemperatureSelection selected = TemperatureReadingSelector.SelectSystem(primary, fallback);

        Assert.NotNull(selected.SystemTemperature);
        Assert.Equal("Windows ACPI", selected.SystemTemperature.Hardware);
        Assert.Equal("System", selected.SystemTemperature.Name);
        Assert.Equal(33.5, selected.SystemTemperature.Celsius);
        Assert.Null(selected.HottestCpuC);
    }

    [Fact]
    public void TemperatureReadingsPreferReadablePrimaryValues()
    {
        TemperatureReading[] primary =
        [
            new("Intel CPU", "CPU Package", 51, true),
            new("Intel CPU", "Core Max", null, true),
            new("GPU", "GPU Core", 65, false)
        ];
        TemperatureReading[] fallback = [new("Windows ACPI", "TZ00", 90, false)];

        TemperatureSelection selected = TemperatureReadingSelector.SelectSystem(primary, fallback);

        Assert.NotNull(selected.SystemTemperature);
        Assert.Equal("GPU", selected.SystemTemperature.Hardware);
        Assert.Equal("System", selected.SystemTemperature.Name);
        Assert.Equal(65, selected.SystemTemperature.Celsius);
        Assert.Equal(51, selected.HottestCpuC);
    }

    [Fact]
    public void SystemTemperatureCollapsesCoreSensorsAndExcludesDistanceToTjMax()
    {
        TemperatureReading[] primary =
        [
            new("Intel CPU", "CPU Core #1", 45, true),
            new("Intel CPU", "CPU Core #2", 50, true),
            new("Intel CPU", "CPU Core #2 Distance to TjMax", 70, true)
        ];

        TemperatureSelection selected = TemperatureReadingSelector.SelectSystem(primary, []);

        Assert.NotNull(selected.SystemTemperature);
        Assert.Equal("System", selected.SystemTemperature.Name);
        Assert.Equal(50, selected.SystemTemperature.Celsius);
        Assert.Equal(50, selected.HottestCpuC);
    }

    [Fact]
    public void LedColorsRepresentDiskAndNetworkDirection()
    {
        LedStateMachine machine = new(new ThermalOptions());
        DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeMilliseconds(0);
        MetricSnapshot snapshot = new(timestamp, 0, [], 50, 0, 0, true, true, false);

        IReadOnlyList<LedColor> leds = machine.Evaluate(snapshot);

        Assert.Equal(LedColor.GreenOnly, leds[0]);
        Assert.Equal(LedColor.Amber, leds[1]);
        Assert.Equal(LedColor.GreenOnly, leds[2]);
        Assert.Equal(LedColor.Off, leds[3]);
    }

    [Fact]
    public void ThermalWarningEntersAtNinetyAndClearsBelowEightyEight()
    {
        LedStateMachine machine = new(new ThermalOptions { TjMaxC = 100, WarningMarginC = 10, ClearHysteresisC = 2 });
        DateTimeOffset onPhase = DateTimeOffset.FromUnixTimeMilliseconds(0);

        Assert.False(machine.Evaluate(Snapshot(onPhase, 89))[3] == LedColor.RedOnly);
        Assert.Equal(LedColor.RedOnly, machine.Evaluate(Snapshot(onPhase, 90))[3]);
        Assert.True(machine.ThermalWarning);
        _ = machine.Evaluate(Snapshot(onPhase, 88));
        Assert.True(machine.ThermalWarning);
        _ = machine.Evaluate(Snapshot(onPhase, 87.9));
        Assert.False(machine.ThermalWarning);
    }

    [Fact]
    public void ThermalLedUsesTwoHertzFlashPhases()
    {
        LedStateMachine machine = new(new ThermalOptions());

        Assert.Equal(LedColor.RedOnly, machine.Evaluate(Snapshot(DateTimeOffset.FromUnixTimeMilliseconds(0), 92))[3]);
        Assert.Equal(LedColor.Off, machine.Evaluate(Snapshot(DateTimeOffset.FromUnixTimeMilliseconds(250), 92))[3]);
        Assert.Equal(LedColor.RedOnly, machine.Evaluate(Snapshot(DateTimeOffset.FromUnixTimeMilliseconds(500), 92))[3]);
    }

    private static MetricSnapshot Snapshot(DateTimeOffset timestamp, double temperature) =>
        new(timestamp, 0, [], temperature, 0, 0, false, false, false);

    private sealed class FakeNetworkProvider : INetworkCounterProvider
    {
        public IReadOnlyList<InterfaceReading> Current { get; set; } = Array.Empty<InterfaceReading>();
        public IReadOnlyList<InterfaceReading> ReadPhysicalInterfaces() => Current;
    }
}
