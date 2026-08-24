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
        PageController pages = Controller(_display, new FakeShutdownExecutor());
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
        PageController pages = Controller(_display, new FakeShutdownExecutor());
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
    public void CpuPageShowsSystemFallbackWithoutClaimingCpuThermalMargin()
    {
        PageController pages = Controller(_display, new FakeShutdownExecutor());
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        pages.HandleKey(CfaKey.Right, now);
        MetricSnapshot snapshot = Snapshot() with
        {
            Temperatures = [new TemperatureReading("Windows ACPI", "TZ00", 31.5, false)],
            HottestCpuC = null
        };

        string[] rows = pages.Render(snapshot, new ThermalOptions());

        Assert.StartsWith("System", rows[2]);
        Assert.Contains("31.5", rows[2]);
        Assert.StartsWith("CPU TEMP UNAVAILABLE", rows[3]);
    }

    [Fact]
    public void ExitDisablesAutoCycleAndReturnsToClock()
    {
        PageController pages = Controller(_display, new FakeShutdownExecutor());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        pages.HandleKey(CfaKey.Enter, now);
        pages.HandleKey(CfaKey.Right, now.AddMilliseconds(200));
        pages.HandleKey(CfaKey.Exit, now.AddMilliseconds(400));

        Assert.False(pages.AutoCycle);
        Assert.Equal(PageCategory.DateTime, pages.Category);
    }

    [Fact]
    public void ShutdownPageIsReachableOnlyByManualNavigation()
    {
        PageController pages = Controller(_display, new FakeShutdownExecutor());
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");

        for (int press = 0; press < 4; press++)
        {
            pages.HandleKey(CfaKey.Right, now.AddMilliseconds(press * 200));
        }

        Assert.Equal(PageCategory.Shutdown, pages.Category);

        pages.HandleKey(CfaKey.Right, now.AddMilliseconds(800));
        Assert.Equal(PageCategory.DateTime, pages.Category);

        pages.HandleKey(CfaKey.Left, now.AddMilliseconds(1000));
        Assert.Equal(PageCategory.Shutdown, pages.Category);
    }

    [Fact]
    public void AutoCycleSkipsShutdownPage()
    {
        PageController pages = Controller(_display, new FakeShutdownExecutor());
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        pages.HandleKey(CfaKey.Enter, now);
        Assert.True(pages.AutoCycle);

        List<PageCategory> visited = [];
        for (int tick = 1; tick <= 12; tick++)
        {
            now = now.AddSeconds(6);
            if (pages.Tick(now))
            {
                visited.Add(pages.Category);
            }
        }

        Assert.DoesNotContain(PageCategory.Shutdown, visited);
        Assert.Contains(PageCategory.Cpu, visited);
        Assert.Contains(PageCategory.Network, visited);
        Assert.Contains(PageCategory.DateTime, visited);
    }

    [Fact]
    public void ConfirmDefaultsToNoAndNoDoesNothing()
    {
        FakeShutdownExecutor executor = new();
        PageController pages = Controller(_display, executor);
        DateTimeOffset now = NavigateToShutdown(pages);

        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        Assert.Equal(ShutdownUiState.Confirm, pages.ShutdownState);
        Assert.False(pages.ConfirmYesSelected);

        pages.HandleKey(CfaKey.Enter, now.AddMilliseconds(200));
        Assert.Equal(ShutdownUiState.Idle, pages.ShutdownState);
        Assert.Equal(PageCategory.Shutdown, pages.Category);
        Assert.Empty(executor.Requested);
        Assert.Equal(0, executor.AbortCount);
    }

    [Fact]
    public void YesTriggersExecutorWithConfiguredSeconds()
    {
        FakeShutdownExecutor executor = new();
        PageController pages = Controller(_display, executor, countdownSeconds: 45);
        DateTimeOffset now = NavigateToShutdown(pages);

        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        pages.HandleKey(CfaKey.Left, now = now.AddMilliseconds(200));
        Assert.True(pages.ConfirmYesSelected);
        pages.HandleKey(CfaKey.Enter, now.AddMilliseconds(200));

        Assert.Equal(new[] { 45 }, executor.Requested);
        Assert.Equal(ShutdownUiState.CountingDown, pages.ShutdownState);
        Assert.False(pages.AutoCycle);
    }

    [Fact]
    public void UpDownAdjustPendingSecondsAndClampAtBounds()
    {
        FakeShutdownExecutor executor = new();
        PageController pages = Controller(_display, executor);
        DateTimeOffset now = NavigateToShutdown(pages);

        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        Assert.Equal(30, pages.PendingSeconds);
        pages.HandleKey(CfaKey.Up, now = now.AddMilliseconds(200));
        pages.HandleKey(CfaKey.Up, now = now.AddMilliseconds(200));
        Assert.Equal(40, pages.PendingSeconds);
        pages.HandleKey(CfaKey.Down, now = now.AddMilliseconds(200));
        Assert.Equal(35, pages.PendingSeconds);

        pages.HandleKey(CfaKey.Left, now = now.AddMilliseconds(200));
        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        Assert.Equal(new[] { 35 }, executor.Requested);

        FakeShutdownExecutor lowExecutor = new();
        PageController lowPages = Controller(_display, lowExecutor, countdownSeconds: ShutdownOptions.MinCountdownSeconds);
        DateTimeOffset lowNow = NavigateToShutdown(lowPages);
        lowPages.HandleKey(CfaKey.Enter, lowNow = lowNow.AddMilliseconds(200));
        lowPages.HandleKey(CfaKey.Down, lowNow.AddMilliseconds(200));
        Assert.Equal(ShutdownOptions.MinCountdownSeconds, lowPages.PendingSeconds);

        FakeShutdownExecutor highExecutor = new();
        PageController highPages = Controller(_display, highExecutor, countdownSeconds: ShutdownOptions.MaxCountdownSeconds);
        DateTimeOffset highNow = NavigateToShutdown(highPages);
        highPages.HandleKey(CfaKey.Enter, highNow = highNow.AddMilliseconds(200));
        highPages.HandleKey(CfaKey.Up, highNow.AddMilliseconds(200));
        Assert.Equal(ShutdownOptions.MaxCountdownSeconds, highPages.PendingSeconds);
    }

    [Fact]
    public void ExitDuringCountdownAbortsAndReturnsToDateTime()
    {
        FakeShutdownExecutor executor = new();
        PageController pages = Controller(_display, executor);
        DateTimeOffset now = StartCountdown(pages);

        pages.HandleKey(CfaKey.Exit, now.AddMilliseconds(200));

        Assert.Equal(1, executor.AbortCount);
        Assert.Equal(ShutdownUiState.Idle, pages.ShutdownState);
        Assert.Equal(PageCategory.DateTime, pages.Category);
        Assert.False(pages.AutoCycle);
    }

    [Fact]
    public void CountdownBlocksPageSwitchingAndTick()
    {
        FakeShutdownExecutor executor = new();
        PageController pages = Controller(_display, executor);
        DateTimeOffset now = StartCountdown(pages);

        Assert.False(pages.HandleKey(CfaKey.Left, now = now.AddMilliseconds(200)));
        Assert.False(pages.HandleKey(CfaKey.Right, now = now.AddMilliseconds(200)));
        Assert.False(pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200)));
        Assert.Equal(PageCategory.Shutdown, pages.Category);
        Assert.Equal(ShutdownUiState.CountingDown, pages.ShutdownState);
        Assert.Single(executor.Requested);

        Assert.False(pages.Tick(now.AddSeconds(30)));
        Assert.Equal(PageCategory.Shutdown, pages.Category);
    }

    [Fact]
    public void ExitInConfirmReturnsWithoutSideEffects()
    {
        FakeShutdownExecutor executor = new();
        PageController pages = Controller(_display, executor);
        DateTimeOffset now = NavigateToShutdown(pages);

        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        pages.HandleKey(CfaKey.Exit, now.AddMilliseconds(200));

        Assert.Equal(ShutdownUiState.Idle, pages.ShutdownState);
        Assert.Equal(PageCategory.Shutdown, pages.Category);
        Assert.Empty(executor.Requested);
        Assert.Equal(0, executor.AbortCount);
    }

    [Fact]
    public void ConfirmAndCountdownRowsAreTwentyAsciiCharacters()
    {
        FakeShutdownExecutor executor = new();
        PageController pages = Controller(_display, executor);
        DateTimeOffset now = NavigateToShutdown(pages);
        MetricSnapshot snapshot = Snapshot();

        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        string[] confirmNo = pages.Render(snapshot, new ThermalOptions());
        AssertTwentyAscii(confirmNo);
        Assert.Contains("SHUTDOWN DEVICE?", confirmNo[0]);
        Assert.Contains(">NO<", confirmNo[2]);

        pages.HandleKey(CfaKey.Left, now = now.AddMilliseconds(200));
        string[] confirmYes = pages.Render(snapshot, new ThermalOptions());
        AssertTwentyAscii(confirmYes);
        Assert.Contains(">YES<", confirmYes[2]);

        pages.HandleKey(CfaKey.Enter, now.AddMilliseconds(200));
        string[] countdown = pages.Render(snapshot, new ThermalOptions());
        AssertTwentyAscii(countdown);
        Assert.Contains("SHUTTING DOWN", countdown[0]);
        Assert.Contains("PRESS X TO CANCEL", countdown[3]);
    }

    [Fact]
    public void CountdownRowShowsDeadlineDerivedSeconds()
    {
        FakeShutdownExecutor executor = new();
        PageController pages = Controller(_display, executor);
        DateTimeOffset confirmedAt = StartCountdown(pages);

        string[] threeSecondsIn = pages.Render(
            Snapshot() with { Timestamp = confirmedAt.AddSeconds(3) }, new ThermalOptions());
        Assert.StartsWith("IN 27s", threeSecondsIn[1]);

        string[] pastDeadline = pages.Render(
            Snapshot() with { Timestamp = confirmedAt.AddSeconds(31) }, new ThermalOptions());
        Assert.StartsWith("IN 0s", pastDeadline[1]);
    }

    private static void AssertTwentyAscii(string[] rows) =>
        Assert.All(rows, row =>
        {
            Assert.Equal(20, row.Length);
            Assert.All(row, character => Assert.InRange(character, ' ', '~'));
        });

    private static PageController Controller(
        DisplayOptions display, FakeShutdownExecutor executor, int countdownSeconds = 30) =>
        new(display, new ShutdownOptions { CountdownSeconds = countdownSeconds }, executor);

    /// <summary>Presses Right four times from DateTime; returns the timestamp of the last press.</summary>
    private static DateTimeOffset NavigateToShutdown(PageController pages)
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        for (int press = 0; press < 4; press++)
        {
            pages.HandleKey(CfaKey.Right, now = press == 0 ? now : now.AddMilliseconds(200));
        }

        Assert.Equal(PageCategory.Shutdown, pages.Category);
        return now;
    }

    /// <summary>Navigates to Shutdown, confirms YES; returns the timestamp of the confirming Enter.</summary>
    private static DateTimeOffset StartCountdown(PageController pages)
    {
        DateTimeOffset now = NavigateToShutdown(pages);
        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        pages.HandleKey(CfaKey.Left, now = now.AddMilliseconds(200));
        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        Assert.Equal(ShutdownUiState.CountingDown, pages.ShutdownState);
        return now;
    }

    private sealed class FakeShutdownExecutor : IShutdownExecutor
    {
        public List<int> Requested { get; } = [];
        public int AbortCount { get; private set; }
        public void RequestShutdown(int seconds) => Requested.Add(seconds);
        public void Abort() => AbortCount++;
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
