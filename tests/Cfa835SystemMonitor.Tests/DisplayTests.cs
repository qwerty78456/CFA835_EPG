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
    public void MainPageShowsCpuAndSingleSystemTemperature()
    {
        PageController pages = Controller(_display, new FakeShutdownExecutor());
        MetricSnapshot snapshot = Snapshot() with
        {
            Temperatures = [new TemperatureReading("Windows ACPI", "System", 31.5, false)],
            HottestCpuC = null
        };

        string[] rows = pages.Render(snapshot, new ThermalOptions());

        Assert.StartsWith("2026-08-17 12:34:56", rows[0]);
        Assert.StartsWith("CPU UTIL", rows[1]);
        Assert.EndsWith("23.4%", rows[1]);
        Assert.StartsWith("TEMPERATURE", rows[2]);
        Assert.Contains("31.5", rows[2]);
        Assert.StartsWith("AUTO: OFF", rows[3]);
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

        for (int press = 0; press < 2; press++)
        {
            pages.HandleKey(CfaKey.Right, now.AddMilliseconds(press * 200));
        }

        Assert.Equal(PageCategory.Shutdown, pages.Category);

        pages.HandleKey(CfaKey.Right, now.AddMilliseconds(600));
        Assert.Equal(PageCategory.DateTime, pages.Category);

        pages.HandleKey(CfaKey.Left, now.AddMilliseconds(800));
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
        Assert.Contains(PageCategory.Network, visited);
        Assert.Contains(PageCategory.DateTime, visited);
        Assert.Equal(2, visited.Distinct().Count());
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
    public void ReplacementCancelsPendingShutdownAndBlocksFurtherKeys()
    {
        FakeShutdownExecutor executor = new();
        PageController pages = Controller(_display, executor);
        DateTimeOffset now = StartCountdown(pages);

        ReplacementPreparationResult result = pages.PrepareForReplacement();

        Assert.True(result.Accepted);
        Assert.True(result.ShutdownWasPending);
        Assert.True(result.ShutdownCancelled);
        Assert.Equal(1, executor.AbortCount);
        Assert.Equal(ShutdownUiState.Idle, pages.ShutdownState);
        Assert.Equal(PageCategory.DateTime, pages.Category);
        Assert.False(pages.HandleKey(CfaKey.Right, now.AddMilliseconds(200)));
    }

    [Fact]
    public void ReplacementContinuesWithoutAbortWhenNoShutdownIsPending()
    {
        FakeShutdownExecutor executor = new();
        PageController pages = Controller(_display, executor);

        ReplacementPreparationResult result = pages.PrepareForReplacement();

        Assert.True(result.Accepted);
        Assert.False(result.ShutdownWasPending);
        Assert.False(result.ShutdownCancelled);
        Assert.Equal(0, executor.AbortCount);
    }

    [Fact]
    public void ReplacementIsRejectedWhenPendingShutdownCannotBeCancelled()
    {
        FakeShutdownExecutor executor = new() { AbortSucceeds = false };
        PageController pages = Controller(_display, executor);
        DateTimeOffset now = StartCountdown(pages);

        ReplacementPreparationResult result = pages.PrepareForReplacement();

        Assert.False(result.Accepted);
        Assert.True(result.ShutdownWasPending);
        Assert.False(result.ShutdownCancelled);
        Assert.Equal(ShutdownUiState.CountingDown, pages.ShutdownState);
        Assert.False(pages.HandleKey(CfaKey.Left, now.AddMilliseconds(200)));
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

    [Fact]
    public void LayoutDefinedPagesDriveTheNavigationRing()
    {
        PageController pages = new(
            _display,
            new ShutdownOptions(),
            new FakeShutdownExecutor(),
            [new PageDescriptor("Main", false), new PageDescriptor("Gpu", false), new PageDescriptor("Bye", true)]);
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

        Assert.Equal("Main", pages.CurrentPageId);
        Assert.True(pages.HandleKey(CfaKey.Right, now));
        Assert.Equal("Gpu", pages.CurrentPageId);
        Assert.True(pages.HandleKey(CfaKey.Right, now = now.AddMilliseconds(200)));
        Assert.Equal("Bye", pages.CurrentPageId);
        // Wrapping stays inside the configured ring rather than the built-in category count.
        Assert.True(pages.HandleKey(CfaKey.Right, now = now.AddMilliseconds(200)));
        Assert.Equal("Main", pages.CurrentPageId);
        Assert.True(pages.HandleKey(CfaKey.Left, now.AddMilliseconds(200)));
        Assert.Equal("Bye", pages.CurrentPageId);
    }

    [Fact]
    public void AutoCycleSkipsLayoutPagesMarkedAsShutdown()
    {
        PageController pages = new(
            _display,
            new ShutdownOptions(),
            new FakeShutdownExecutor(),
            [new PageDescriptor("Main", false), new PageDescriptor("Bye", true), new PageDescriptor("Gpu", false)]);
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        pages.HandleKey(CfaKey.Enter, now);

        List<string> visited = [];
        for (int step = 0; step < 6; step++)
        {
            now = now.AddSeconds(_display.AutoCycleSeconds + 1);
            pages.Tick(now);
            visited.Add(pages.CurrentPageId);
        }

        Assert.DoesNotContain("Bye", visited);
        Assert.Contains("Main", visited);
        Assert.Contains("Gpu", visited);
    }

    [Fact]
    public void EnterOnALayoutShutdownPageStartsTheConfirmFlow()
    {
        FakeShutdownExecutor executor = new();
        PageController pages = new(
            _display,
            new ShutdownOptions { CountdownSeconds = 20 },
            executor,
            [new PageDescriptor("Main", false), new PageDescriptor("Bye", true)]);
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

        pages.HandleKey(CfaKey.Right, now);
        Assert.Equal("Bye", pages.CurrentPageId);
        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        Assert.Equal(ShutdownUiState.Confirm, pages.ShutdownState);

        pages.HandleKey(CfaKey.Left, now = now.AddMilliseconds(200));
        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));

        Assert.Equal(ShutdownUiState.CountingDown, pages.ShutdownState);
        Assert.Equal([20], executor.Requested);
        Assert.Equal(20, pages.RemainingSeconds(now));
        Assert.Equal(15, pages.RemainingSeconds(now.AddSeconds(5)));
        Assert.Equal(0, pages.RemainingSeconds(now.AddSeconds(90)));
    }

    private static readonly PageDescriptor[] MainGpuBye =
        [new("Main", false), new("Gpu", false), new("Bye", true)];

    [Fact]
    public void ReconfigureKeepsTheCurrentPageWhenItsIdSurvives()
    {
        PageController pages = new(_display, new ShutdownOptions(), new FakeShutdownExecutor(), MainGpuBye);
        pages.HandleKey(CfaKey.Right, DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        Assert.Equal("Gpu", pages.CurrentPageId);

        // Reordered and extended: the operator stays on the page they were looking at.
        Assert.True(pages.TryReconfigure(
            _display,
            new ShutdownOptions(),
            [new PageDescriptor("Intro", false), new PageDescriptor("Main", false), new PageDescriptor("Gpu", false)]));

        Assert.Equal("Gpu", pages.CurrentPageId);
        Assert.Equal(2, pages.CurrentPageIndex);
    }

    [Fact]
    public void ReconfigureMatchesPageIdsIgnoringCase()
    {
        PageController pages = new(_display, new ShutdownOptions(), new FakeShutdownExecutor(), MainGpuBye);
        pages.HandleKey(CfaKey.Right, DateTimeOffset.Parse("2026-08-25T12:00:00Z"));

        Assert.True(pages.TryReconfigure(
            _display, new ShutdownOptions(), [new PageDescriptor("MAIN", false), new PageDescriptor("gpu", false)]));

        Assert.Equal("gpu", pages.CurrentPageId);
    }

    [Fact]
    public void ReconfigureFallsBackToTheFirstPageWhenTheCurrentPageWasRemoved()
    {
        PageController pages = new(_display, new ShutdownOptions(), new FakeShutdownExecutor(), MainGpuBye);
        pages.HandleKey(CfaKey.Right, DateTimeOffset.Parse("2026-08-25T12:00:00Z"));

        Assert.True(pages.TryReconfigure(
            _display, new ShutdownOptions(), [new PageDescriptor("Main", false), new PageDescriptor("Bye", true)]));

        Assert.Equal("Main", pages.CurrentPageId);
    }

    [Fact]
    public void ReconfigureToAShorterRingNeverLeavesTheIndexOutOfRange()
    {
        PageController pages = new(_display, new ShutdownOptions(), new FakeShutdownExecutor(), MainGpuBye);
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        pages.HandleKey(CfaKey.Left, now);
        Assert.Equal(2, pages.CurrentPageIndex);

        Assert.True(pages.TryReconfigure(_display, new ShutdownOptions(), [new PageDescriptor("Solo", false)]));

        Assert.Equal(0, pages.CurrentPageIndex);
        Assert.Equal("Solo", pages.View(now).PageId);
        Assert.True(pages.HandleKey(CfaKey.Right, now.AddMilliseconds(200)));
        Assert.Equal("Solo", pages.CurrentPageId);
    }

    [Fact]
    public void ReconfigurePreservesAutoCycleState()
    {
        // autoCycleOnStart is a start-up value; the operator's live AUTO choice survives a reload.
        PageController pages = new(_display, new ShutdownOptions(), new FakeShutdownExecutor(), MainGpuBye);
        pages.HandleKey(CfaKey.Enter, DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        Assert.True(pages.AutoCycle);

        Assert.True(pages.TryReconfigure(_display with { AutoCycleOnStart = false }, new ShutdownOptions(), MainGpuBye));

        Assert.True(pages.AutoCycle);
    }

    [Fact]
    public void ReconfigureAppliesTheNewAutoCycleInterval()
    {
        PageController pages = new(_display, new ShutdownOptions(), new FakeShutdownExecutor(), MainGpuBye);
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        pages.HandleKey(CfaKey.Enter, now);

        Assert.True(pages.TryReconfigure(_display with { AutoCycleSeconds = 30 }, new ShutdownOptions(), MainGpuBye));

        // The first tick after the reload re-seeds the deadline from the new 30 s interval.
        Assert.False(pages.Tick(now = now.AddSeconds(1)));
        Assert.False(pages.Tick(now.AddSeconds(29)));
        Assert.True(pages.Tick(now.AddSeconds(30)));
        Assert.Equal("Gpu", pages.CurrentPageId);
    }

    [Fact]
    public void ReconfigureAppliesTheNewCountdownDefault()
    {
        PageController pages = Controller(_display, new FakeShutdownExecutor(), countdownSeconds: 30);

        Assert.True(pages.TryReconfigure(_display, new ShutdownOptions { CountdownSeconds = 45 }, null));

        Assert.Equal(45, pages.PendingSeconds);
        DateTimeOffset now = NavigateToShutdown(pages);
        Assert.Contains("Timeout: 45s", pages.Render(Snapshot(), new ThermalOptions())[3]);
        pages.HandleKey(CfaKey.Enter, now.AddMilliseconds(200));
        Assert.Equal(45, pages.PendingSeconds);
    }

    [Fact]
    public void ReconfigureIsRefusedWhileTheConfirmDialogIsOpen()
    {
        PageController pages = Controller(_display, new FakeShutdownExecutor());
        DateTimeOffset now = NavigateToShutdown(pages);
        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        pages.HandleKey(CfaKey.Up, now.AddMilliseconds(200));
        Assert.Equal(ShutdownUiState.Confirm, pages.ShutdownState);

        Assert.False(pages.TryReconfigure(
            _display, new ShutdownOptions { CountdownSeconds = 10 }, [new PageDescriptor("Other", false)]));

        Assert.Equal(ShutdownUiState.Confirm, pages.ShutdownState);
        Assert.Equal("Shutdown", pages.CurrentPageId);
        Assert.Equal(35, pages.PendingSeconds);
    }

    [Fact]
    public void ReconfigureIsRefusedDuringACountdownAndLeavesTheArmedShutdownAlone()
    {
        FakeShutdownExecutor executor = new();
        PageController pages = Controller(_display, executor);
        DateTimeOffset confirmedAt = StartCountdown(pages);

        Assert.False(pages.TryReconfigure(
            _display, new ShutdownOptions { CountdownSeconds = 10 }, [new PageDescriptor("Other", false)]));

        Assert.Equal(ShutdownUiState.CountingDown, pages.ShutdownState);
        Assert.Equal("Shutdown", pages.CurrentPageId);
        Assert.Equal(27, pages.RemainingSeconds(confirmedAt.AddSeconds(3)));
        Assert.Equal(0, executor.AbortCount);

        // Exit still routes to the abort, and only then does a reload go through.
        pages.HandleKey(CfaKey.Exit, confirmedAt.AddMilliseconds(200));
        Assert.Equal(1, executor.AbortCount);
        Assert.True(pages.TryReconfigure(_display, new ShutdownOptions(), [new PageDescriptor("Other", false)]));
    }

    [Fact]
    public void ReconfigureIsRefusedOnceReplacementIsPending()
    {
        PageController pages = Controller(_display, new FakeShutdownExecutor());
        Assert.True(pages.PrepareForReplacement().Accepted);

        Assert.False(pages.TryReconfigure(_display, new ShutdownOptions(), [new PageDescriptor("Other", false)]));

        Assert.Equal("DateTime", pages.CurrentPageId);
    }

    [Fact]
    public void ReconfigureWithoutPagesRestoresTheBuiltInRing()
    {
        // Switching graphic -> text hands the controller no layout pages.
        PageController pages = new(_display, new ShutdownOptions(), new FakeShutdownExecutor(),
            [new PageDescriptor("Network", false), new PageDescriptor("Art", false)]);

        Assert.True(pages.TryReconfigure(_display, new ShutdownOptions(), null));

        Assert.Equal(["DateTime", "Network", "Shutdown"], pages.Pages.Select(page => page.Id));
        Assert.Equal(PageCategory.Network, pages.Category);
    }

    [Fact]
    public void ViewReturnsOneConsistentSnapshot()
    {
        PageController pages = Controller(_display, new FakeShutdownExecutor(), countdownSeconds: 20);
        DateTimeOffset now = NavigateToShutdown(pages);
        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        pages.HandleKey(CfaKey.Left, now = now.AddMilliseconds(200));

        PageView view = pages.View(now);

        Assert.Equal("Shutdown", view.PageId);
        Assert.Equal(PageCategory.Shutdown, view.Category);
        Assert.Equal(ShutdownUiState.Confirm, view.ShutdownState);
        Assert.True(view.ConfirmYesSelected);
        Assert.Equal(20, view.PendingSeconds);
        Assert.False(view.AutoCycle);
    }

    private static PageController Controller(
        DisplayOptions display, FakeShutdownExecutor executor, int countdownSeconds = 30) =>
        new(display, new ShutdownOptions { CountdownSeconds = countdownSeconds }, executor);

    /// <summary>Presses Right twice from DateTime; returns the timestamp of the last press.</summary>
    private static DateTimeOffset NavigateToShutdown(PageController pages)
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        for (int press = 0; press < 2; press++)
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
