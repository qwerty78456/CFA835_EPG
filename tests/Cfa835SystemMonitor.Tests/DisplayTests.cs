namespace Cfa835SystemMonitor.Tests;

public sealed class DisplayTests
{
    private readonly DisplayOptions _display = new()
    {
        AutoCycleOnStart = false,
        AutoCycleSeconds = 5,
        DateFormat = "yyyy-MM-dd",
        TimeFormat = "HH:mm:ss"
    };

    [Fact]
    public void EveryRenderedRowIsExactlyTwentyAsciiCharacters()
    {
        PageController pages = new(_display);
        MetricSnapshot snapshot = Snapshot() with
        {
            Temperatures =
            [
                new TemperatureReading("Very long hardware label", "Very long sensor label", 42.25, false)
            ]
        };

        foreach (PageCategory expected in Enum.GetValues<PageCategory>())
        {
            while (pages.Category != expected)
            {
                pages.HandleKey(CfaKey.Right, snapshot.Timestamp.AddMilliseconds(200 + ((int)pages.Category * 200)));
            }

            string[] rows = pages.Render(snapshot, new ThermalOptions());
            Assert.All(rows, row =>
            {
                Assert.Equal(20, row.Length);
                Assert.All(row, character => Assert.InRange(character, ' ', '~'));
            });
        }
    }

    [Fact]
    public void TemperatureKeysPaginateThreeSensorsAtATime()
    {
        PageController pages = new(_display);
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        pages.HandleKey(CfaKey.Right, now);
        pages.HandleKey(CfaKey.Right, now.AddMilliseconds(200));
        MetricSnapshot snapshot = Snapshot() with
        {
            Temperatures = Enumerable.Range(1, 4)
                .Select(index => new TemperatureReading("HW", $"Sensor {index}", index * 10, index == 1))
                .ToArray()
        };

        string[] first = pages.Render(snapshot, new ThermalOptions());
        pages.HandleKey(CfaKey.Down, now.AddMilliseconds(400));
        string[] second = pages.Render(snapshot, new ThermalOptions());

        Assert.StartsWith("TEMPS 01-03/04", first[0]);
        Assert.StartsWith("TEMPS 04-04/04", second[0]);
        Assert.Contains("Sensor 4", second[1]);
    }

    [Fact]
    public void ExitDisablesAutoCycleAndReturnsToClock()
    {
        PageController pages = new(_display);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        pages.HandleKey(CfaKey.Enter, now);
        pages.HandleKey(CfaKey.Right, now.AddMilliseconds(200));
        pages.HandleKey(CfaKey.Exit, now.AddMilliseconds(400));

        Assert.False(pages.AutoCycle);
        Assert.Equal(PageCategory.DateTime, pages.Category);
    }

    private static MetricSnapshot Snapshot() => new(
        DateTimeOffset.Parse("2026-08-17T12:34:56+07:00"),
        23.4,
        [],
        72,
        12.34,
        1.23,
        false,
        false,
        false);
}
