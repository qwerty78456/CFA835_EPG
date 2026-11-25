using Microsoft.Extensions.Logging.Abstractions;

namespace Cfa835SystemMonitor.Tests;

public sealed class RuntimeStateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-25T08:00:00Z");

    [Fact]
    public void ApplySwapsOptionsAndGraphicAndAdvancesTheGeneration()
    {
        (MonitorRuntimeState state, _) = State(Text());
        ConfigSnapshot graphic = Graphic(gamma: true, "Main", "Net");

        ReloadChanges? changes = state.TryApply(graphic);

        Assert.NotNull(changes);
        Assert.Same(graphic, state.Snapshot);
        Assert.Same(graphic.Graphic, state.Graphic);
        Assert.Equal(1, state.Generation);
    }

    [Fact]
    public void ApplyIsDeferredWhileAShutdownDialogIsOpenAndChangesNothing()
    {
        ConfigSnapshot running = Text();
        (MonitorRuntimeState state, PageController pages) = State(running);
        DateTimeOffset now = Now;
        pages.HandleKey(CfaKey.Left, now);
        Assert.Equal("Shutdown", pages.CurrentPageId);
        pages.HandleKey(CfaKey.Enter, now.AddMilliseconds(200));
        Assert.Equal(ShutdownUiState.Confirm, pages.ShutdownState);

        ReloadChanges? changes = state.TryApply(Graphic(gamma: true, "Main"));

        Assert.Null(changes);
        Assert.Same(running, state.Snapshot);
        Assert.Equal(0, state.Generation);
        Assert.False(state.GraphicEverActive);
        Assert.Equal("Shutdown", pages.CurrentPageId);
    }

    [Fact]
    public void ADeferredReloadAppliesOnceTheDialogCloses()
    {
        (MonitorRuntimeState state, PageController pages) = State(Text());
        DateTimeOffset now = Now;
        pages.HandleKey(CfaKey.Left, now);
        pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        ConfigSnapshot candidate = Graphic(gamma: true, "Main");
        Assert.Null(state.TryApply(candidate));

        pages.HandleKey(CfaKey.Exit, now.AddMilliseconds(200));

        Assert.NotNull(state.TryApply(candidate));
        Assert.Same(candidate, state.Snapshot);
    }

    [Fact]
    public void ApplyRemapsTheControllerOntoTheNewLayoutPages()
    {
        (MonitorRuntimeState state, PageController pages) = State(Text());
        pages.HandleKey(CfaKey.Right, Now);
        Assert.Equal("Network", pages.CurrentPageId);

        state.TryApply(Graphic(gamma: true, "DateTime", "Art", "Network"));

        Assert.Equal(["DateTime", "Art", "Network"], pages.Pages.Select(page => page.Id));
        Assert.Equal("Network", pages.CurrentPageId);
    }

    [Fact]
    public void CompareReportsWhichOptionGroupsChanged()
    {
        ConfigSnapshot before = Text();
        ConfigSnapshot after = new(
            new MonitorOptions
            {
                Thermal = new ThermalOptions { TjMaxC = 95 },
                Device = new DeviceOptions { FallbackPort = "COM7" }
            },
            null,
            []);

        ReloadChanges changes = MonitorRuntimeState.Compare(before, after);

        Assert.Equal(ReloadChanges.Thermal | ReloadChanges.Device, changes);
    }

    [Fact]
    public void CompareFlagsAModeSwitchAndTheNewLayout()
    {
        ReloadChanges changes = MonitorRuntimeState.Compare(Text(), Graphic(gamma: true, "Main"));

        Assert.True(changes.HasFlag(ReloadChanges.DisplayMode));
        Assert.True(changes.HasFlag(ReloadChanges.Layout));
    }

    [Fact]
    public void GraphicEverActiveSticksAfterSwitchingBackToText()
    {
        (MonitorRuntimeState state, _) = State(Text());
        Assert.False(state.GraphicEverActive);

        state.TryApply(Graphic(gamma: true, "Main"));
        state.TryApply(Text());

        Assert.Null(state.Graphic);
        Assert.True(state.GraphicEverActive);
    }

    [Theory]
    // applied state      -> target            graphic ever active, expected
    [InlineData("unknown", "graphic-gamma", false, DisplayTransition.InitializeGraphic)]
    [InlineData("graphic-gamma", "graphic-gamma", true, DisplayTransition.RepaintOnly)]
    [InlineData("graphic-gamma", "graphic-flat", true, DisplayTransition.InitializeGraphic)]
    [InlineData("graphic-flat", "graphic-gamma", true, DisplayTransition.InitializeGraphic)]
    [InlineData("text", "graphic-gamma", false, DisplayTransition.InitializeGraphic)]
    [InlineData("graphic-gamma", "text", true, DisplayTransition.RestoreText)]
    [InlineData("text", "text", false, DisplayTransition.None)]
    [InlineData("text", "text", true, DisplayTransition.None)]
    [InlineData("unknown", "text", false, DisplayTransition.None)]
    [InlineData("unknown", "text", true, DisplayTransition.RestoreText)]
    public void PlanTransitionCoversEveryModePair(string applied, string target, bool graphicEverActive, DisplayTransition expected)
    {
        DeviceDisplay current = applied switch
        {
            "unknown" => default,
            "text" => new DeviceDisplay(DisplayMode.Text, false),
            "graphic-gamma" => new DeviceDisplay(DisplayMode.Graphic, true),
            _ => new DeviceDisplay(DisplayMode.Graphic, false)
        };
        GraphicRuntime? graphic = target switch
        {
            "graphic-gamma" => GraphicWriterTests.Runtime(GraphicWriterTests.Layout(true, GraphicWriterTests.Page("Main"))),
            "graphic-flat" => GraphicWriterTests.Runtime(GraphicWriterTests.Layout(false, GraphicWriterTests.Page("Main"))),
            _ => null
        };

        Assert.Equal(expected, MonitorRuntimeState.PlanTransition(current, graphic, graphicEverActive));
    }

    [Fact]
    public async Task RestoreTextTransitionClearsAndReturnsToAutomaticFlush()
    {
        (FakeTransport transport, Cfa835Device device) = await GraphicWriterTests.OpenAsync();
        await using Cfa835Device _ = device;
        ScreenWriter screen = new();
        await screen.RenderAsync(device, Rows("A"), CancellationToken.None);
        transport.Log.Clear();
        transport.Commands.Clear();

        GraphicScreenWriter? writer = await DisplayTransitions.ExecuteAsync(
            DisplayTransition.RestoreText, device, null, screen, NullLogger<GraphicScreenWriter>.Instance, CancellationToken.None);

        Assert.Null(writer);
        Assert.Equal(["cmd:0x06", "cmd:0x28/0"], transport.Log);
        Assert.Equal([0, 0], transport.Commands[^1].Data);

        // The row cache was reset with the panel, so the same four rows are written again.
        transport.Log.Clear();
        await screen.RenderAsync(device, Rows("A"), CancellationToken.None);
        Assert.Equal(4, transport.Log.Count(entry => entry.StartsWith("cmd:0x1F", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RepaintOnlyTransitionSendsNoDeviceCommands()
    {
        (FakeTransport transport, Cfa835Device device) = await GraphicWriterTests.OpenAsync();
        await using Cfa835Device _ = device;
        GraphicRuntime runtime = GraphicWriterTests.Runtime(GraphicWriterTests.Layout(true, GraphicWriterTests.Page("Main")));

        GraphicScreenWriter? writer = await DisplayTransitions.ExecuteAsync(
            DisplayTransition.RepaintOnly, device, runtime, new ScreenWriter(), NullLogger<GraphicScreenWriter>.Instance, CancellationToken.None);

        Assert.NotNull(writer);
        Assert.Empty(transport.Log);
    }

    [Fact]
    public async Task InitializeGraphicTransitionClearsThenEnablesManualFlushWithTheNewGamma()
    {
        (FakeTransport transport, Cfa835Device device) = await GraphicWriterTests.OpenAsync();
        await using Cfa835Device _ = device;
        GraphicRuntime runtime = GraphicWriterTests.Runtime(GraphicWriterTests.Layout(false, GraphicWriterTests.Page("Main")));

        GraphicScreenWriter? writer = await DisplayTransitions.ExecuteAsync(
            DisplayTransition.InitializeGraphic, device, runtime, new ScreenWriter(), NullLogger<GraphicScreenWriter>.Instance, CancellationToken.None);

        Assert.NotNull(writer);
        Assert.Equal(["cmd:0x06", "cmd:0x28/0"], transport.Log);
        Assert.Equal([0, 0x01], transport.Commands[^1].Data);
    }

    [Fact]
    public async Task NoTextRowIsWrittenAfterSwitchingToGraphic()
    {
        // Text (0x1F) and composited graphic (0x28) output must never share the panel: the clear has
        // to come before the first graphic frame, and no row write may follow it.
        (FakeTransport transport, Cfa835Device device) = await GraphicWriterTests.OpenAsync();
        await using Cfa835Device _ = device;
        ScreenWriter screen = new();
        await screen.RenderAsync(device, Rows("text mode"), CancellationToken.None);
        (MonitorRuntimeState state, PageController pages) = State(Text());
        Assert.NotNull(state.TryApply(Graphic(gamma: true, "DateTime")));

        DisplayTransition transition = MonitorRuntimeState.PlanTransition(
            new DeviceDisplay(DisplayMode.Text, false), state.Graphic, state.GraphicEverActive);
        GraphicScreenWriter? writer = await DisplayTransitions.ExecuteAsync(
            transition, device, state.Graphic, screen, NullLogger<GraphicScreenWriter>.Instance, CancellationToken.None);
        Assert.NotNull(writer);
        await writer.RenderAsync(
            device, state.Graphic!.Page(pages.CurrentPageId), GraphicWriterTests.Context(), Now, CancellationToken.None);

        int clear = transport.Log.IndexOf("cmd:0x06");
        int firstFrame = transport.Log.IndexOf("stream:0x28");
        Assert.InRange(clear, 0, firstFrame - 1);
        Assert.DoesNotContain(transport.Log.Skip(clear), entry => entry.StartsWith("cmd:0x1F", StringComparison.Ordinal));
        Assert.Equal("cmd:0x28/1", transport.Log[^1]);
    }

    [Fact]
    public void AfterRecordsWhatTheConnectionWasInitialisedTo()
    {
        GraphicRuntime flat = GraphicWriterTests.Runtime(GraphicWriterTests.Layout(false, GraphicWriterTests.Page("Main")));

        Assert.Equal(new DeviceDisplay(DisplayMode.Graphic, false), MonitorRuntimeState.After(flat));
        Assert.Equal(new DeviceDisplay(DisplayMode.Text, false), MonitorRuntimeState.After(null));
    }

    private static (MonitorRuntimeState State, PageController Pages) State(ConfigSnapshot initial)
    {
        PageController pages = new(
            initial.Options.Display,
            initial.Options.Shutdown,
            new FakeShutdownExecutor(),
            initial.Graphic?.Layout.Descriptors());
        return (new MonitorRuntimeState(initial, pages), pages);
    }

    private static ConfigSnapshot Text() => new(new MonitorOptions(), null, []);

    private static ConfigSnapshot Graphic(bool gamma, params string[] pageIds)
    {
        // ResolvedMode is only set by validation, which runs inside MonitorOptions.Load.
        string path = Path.Combine(Path.GetTempPath(), $"cfa835-state-{Guid.NewGuid():N}.json");
        MonitorOptions options;
        try
        {
            File.WriteAllText(path, """{ "display": { "mode": "graphic" } }""");
            options = MonitorOptions.Load(path);
        }
        finally
        {
            File.Delete(path);
        }

        GraphicRuntime runtime = GraphicWriterTests.Runtime(
            GraphicWriterTests.Layout(gamma, pageIds.Select(id => GraphicWriterTests.Page(id)).ToArray()));
        return new ConfigSnapshot(options, runtime, []);
    }

    private static string[] Rows(string first) => [first, "row 2", "row 3", "row 4"];
}
