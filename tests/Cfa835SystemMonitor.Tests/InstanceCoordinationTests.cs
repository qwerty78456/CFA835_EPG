using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cfa835SystemMonitor.Tests;

public sealed class InstanceCoordinationTests
{
    [Theory]
    [InlineData(AppMode.Monitor)]
    [InlineData(AppMode.Diagnose)]
    [InlineData(AppMode.HardwareTest)]
    public void HardwareModesRequireExclusiveDeviceOwnership(AppMode mode)
    {
        Assert.True(InstanceCoordinator.UsesCfaDevice(mode));
    }

    [Theory]
    [InlineData(AppMode.LayoutPreview)]
    [InlineData(AppMode.ListSensors)]
    public void NonHardwareModesDoNotDisruptTheRunningMonitor(AppMode mode)
    {
        Assert.False(InstanceCoordinator.UsesCfaDevice(mode));
    }

    [Fact]
    public void ReplacementResultPreservesShutdownCancellationEvidence()
    {
        ReplacementPreparationResult result = ReplacementPreparationResult.Ready(
            shutdownWasPending: true,
            shutdownCancelled: true);

        Assert.True(result.Accepted);
        Assert.True(result.ShutdownWasPending);
        Assert.True(result.ShutdownCancelled);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData(InstanceRunContext.Foreground, InstanceRunContext.Foreground, true)]
    [InlineData(InstanceRunContext.Foreground, InstanceRunContext.Service, false)]
    [InlineData(InstanceRunContext.Service, InstanceRunContext.Foreground, false)]
    [InlineData(InstanceRunContext.Service, InstanceRunContext.Service, false)]
    public void OnlyForegroundInstancesReplaceOtherForegroundInstances(
        InstanceRunContext requester,
        InstanceRunContext owner,
        bool expected)
    {
        Assert.Equal(expected, InstanceCoordinator.MayReplace(requester, owner));
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly InstanceDescriptor OwnerProcess = new(
        100,
        new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc),
        @"C:\Program Files\Cfa835SystemMonitor\Cfa835SystemMonitor.exe",
        "1.0.0",
        AppMode.Monitor,
        1,
        InstanceRunContext.Foreground);

    private static readonly InstanceDescriptor ForegroundRequester = OwnerProcess with
    {
        Pid = 200,
        StartTimeUtc = OwnerProcess.StartTimeUtc.AddMinutes(5)
    };

    [Fact]
    public async Task DeliveredAcceptanceStopsTheOwner()
    {
        RunningOwner owner = new();
        ScriptedConnection connection = new(Request(ForegroundRequester));

        await owner.Handler.HandleAsync(connection, CancellationToken.None);

        TakeoverResponse response = Response(connection);
        Assert.True(response.Accepted);
        Assert.Equal(OwnerProcess.Pid, response.Owner.Pid);
        Assert.Equal(1, owner.Stops);
        Assert.True(owner.Pages.ReplacementPending);
        Assert.Empty(owner.Processes.WaitedFor);
    }

    [Fact]
    public async Task UndeliveredAcceptanceResumesOnlyOnceTheRequesterHasExited()
    {
        RunningOwner owner = new();
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-25T08:00:00Z");

        Task handling = owner.Handler.HandleAsync(
            new ScriptedConnection(Request(ForegroundRequester)) { FailWrites = true },
            CancellationToken.None);

        // The requester may still be alive and about to abort any shutdown and terminate this
        // owner, so nothing is re-enabled while it runs.
        Assert.False(handling.IsCompleted);
        Assert.Equal([ForegroundRequester.Pid], owner.Processes.WaitedFor);
        Assert.True(owner.Pages.ReplacementPending);
        Assert.False(owner.Pages.HandleKey(CfaKey.Right, now));
        Assert.False(owner.Pages.TryReconfigure(new DisplayOptions(), new ShutdownOptions(), null));

        owner.Processes.RequesterExit.SetResult(true);
        await handling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, owner.Stops);
        Assert.False(owner.Pages.ReplacementPending);
        Assert.True(owner.Pages.HandleKey(CfaKey.Right, now.AddMilliseconds(200)));
        Assert.True(owner.Pages.TryReconfigure(new DisplayOptions(), new ShutdownOptions(), null));
        Assert.Contains(owner.Logger.At(LogLevel.Information), message => message.Contains("exited without taking over"));
    }

    [Fact]
    public async Task UndeliveredAcceptanceStaysPausedWhenTheRequesterCannotBeTracked()
    {
        RunningOwner owner = new();
        owner.Processes.RequesterExit.SetResult(false);

        await owner.Handler.HandleAsync(
            new ScriptedConnection(Request(ForegroundRequester)) { FailWrites = true },
            CancellationToken.None);

        Assert.Equal(0, owner.Stops);
        Assert.True(owner.Pages.ReplacementPending);
        Assert.Contains(owner.Logger.At(LogLevel.Error), message => message.Contains("Restart the monitor"));
    }

    [Fact]
    public async Task UndeliveredAcceptanceLeavesTheCancelledCountdownCancelled()
    {
        RunningOwner owner = new();
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-25T08:00:00Z");
        owner.Pages.HandleKey(CfaKey.Left, now);
        owner.Pages.HandleKey(CfaKey.Enter, now = now.AddMilliseconds(200));
        owner.Pages.HandleKey(CfaKey.Left, now = now.AddMilliseconds(200));
        owner.Pages.HandleKey(CfaKey.Enter, now.AddMilliseconds(200));
        Assert.Equal(ShutdownUiState.CountingDown, owner.Pages.ShutdownState);
        owner.Processes.RequesterExit.SetResult(true);

        await owner.Handler.HandleAsync(
            new ScriptedConnection(Request(ForegroundRequester)) { FailWrites = true },
            CancellationToken.None);

        Assert.False(owner.Pages.ReplacementPending);
        Assert.Equal(1, owner.Executor.AbortCount);
        Assert.Single(owner.Executor.Requested);
        Assert.Equal(ShutdownUiState.Idle, owner.Pages.ShutdownState);
        Assert.Equal(PageCategory.DateTime, owner.Pages.Category);
        Assert.Contains(owner.Logger.At(LogLevel.Warning), message => message.Contains("stays cancelled"));
    }

    [Fact]
    public async Task CancellationWhileWaitingForTheRequesterKeepsTheOwnerPaused()
    {
        RunningOwner owner = new();
        using CancellationTokenSource stopping = new();

        Task handling = owner.Handler.HandleAsync(
            new ScriptedConnection(Request(ForegroundRequester)) { FailWrites = true },
            stopping.Token);
        stopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handling);
        Assert.True(owner.Pages.ReplacementPending);
        Assert.Equal(0, owner.Stops);
    }

    [Fact]
    public async Task ServiceRequesterIsRejectedWithoutPausingTheOwner()
    {
        RunningOwner owner = new();
        ScriptedConnection connection = new(Request(ForegroundRequester with { RunContext = InstanceRunContext.Service }));

        await owner.Handler.HandleAsync(connection, CancellationToken.None);

        TakeoverResponse response = Response(connection);
        Assert.False(response.Accepted);
        Assert.Contains("do not replace each other", response.Reason);
        Assert.False(owner.Pages.ReplacementPending);
        Assert.Equal(0, owner.Stops);
    }

    [Fact]
    public async Task ForegroundRequesterCannotReplaceAServiceOwner()
    {
        RunningOwner owner = new(InstanceRunContext.Service);
        ScriptedConnection connection = new(Request(ForegroundRequester));

        await owner.Handler.HandleAsync(connection, CancellationToken.None);

        Assert.False(Response(connection).Accepted);
        Assert.False(owner.Pages.ReplacementPending);
        Assert.Equal(0, owner.Stops);
    }

    [Fact]
    public async Task UnverifiedRequesterIsRejectedWithoutPausingTheOwner()
    {
        RunningOwner owner = new(verificationError: "unexpected product metadata");
        ScriptedConnection connection = new(Request(ForegroundRequester));

        await owner.Handler.HandleAsync(connection, CancellationToken.None);

        TakeoverResponse response = Response(connection);
        Assert.False(response.Accepted);
        Assert.Contains("unexpected product metadata", response.Reason);
        Assert.False(owner.Pages.ReplacementPending);
    }

    [Fact]
    public async Task UndeliveredRejectionChangesNothing()
    {
        RunningOwner owner = new();
        ScriptedConnection connection = new(Request(ForegroundRequester with { RunContext = InstanceRunContext.Service }))
        {
            FailWrites = true
        };

        await Assert.ThrowsAsync<IOException>(() => owner.Handler.HandleAsync(connection, CancellationToken.None));

        Assert.False(owner.Pages.ReplacementPending);
        Assert.Empty(owner.Processes.WaitedFor);
        Assert.Equal(0, owner.Stops);
    }

    [Fact]
    public async Task ControlServerServesTheNextRequestAfterAnUndeliverableAcceptance()
    {
        RunningOwner owner = new();
        owner.Processes.RequesterExit.SetResult(true);
        ScriptedConnection abandoned = new(Request(ForegroundRequester)) { FailWrites = true };
        ScriptedConnection next = new(Request(ForegroundRequester));
        FakeControlListener listener = new(abandoned, next);
        using CancellationTokenSource stopping = new();

        Task server = ControlServer.RunAsync(listener, owner.Handler.HandleAsync, owner.Logger, stopping.Token);
        await listener.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(Response(next).Accepted);
        Assert.Equal(1, owner.Stops);
        Assert.True(abandoned.Disposed);
        Assert.True(next.Disposed);
        Assert.True(listener.Disposed);
    }

    [Fact]
    public async Task ControlServerKeepsServingAfterARequestThrows()
    {
        RecordingLogger logger = new();
        ScriptedConnection first = new("");
        ScriptedConnection second = new("");
        FakeControlListener listener = new(first, second);
        List<Stream> handled = [];
        using CancellationTokenSource stopping = new();

        Task server = ControlServer.RunAsync(
            listener,
            (connection, _) =>
            {
                handled.Add(connection);
                return handled.Count == 1
                    ? Task.FromException(new IOException("Pipe is broken."))
                    : Task.CompletedTask;
            },
            logger,
            stopping.Token);
        await listener.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal<Stream>([first, second], handled);
        Assert.Single(logger.At(LogLevel.Warning), message => message.Contains("stays open"));
    }

    [Fact]
    public async Task ControlServerStopsWhenItCannotListen()
    {
        RecordingLogger logger = new();
        FakeControlListener listener = new() { FailWhenEmpty = new UnauthorizedAccessException("pipe name taken") };

        await ControlServer.RunAsync(listener, (_, _) => Task.CompletedTask, logger, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(logger.At(LogLevel.Warning), message => message.Contains("stopped unexpectedly"));
        Assert.True(listener.Disposed);
    }

    private static string Request(InstanceDescriptor requester) =>
        JsonSerializer.Serialize(new TakeoverRequest(HandoverRequestHandler.ProtocolVersion, requester), Json) + "\r\n";

    private static TakeoverResponse Response(ScriptedConnection connection) =>
        JsonSerializer.Deserialize<TakeoverResponse>(connection.Response, Json)!;

    /// <summary>A running owner: a real <see cref="PageController"/> behind the handover endpoint.</summary>
    private sealed class RunningOwner
    {
        public RunningOwner(InstanceRunContext runContext = InstanceRunContext.Foreground, string? verificationError = null)
        {
            Pages = new(new DisplayOptions { AutoCycleOnStart = false }, new ShutdownOptions(), Executor);
            Processes = new(OwnerProcess with { RunContext = runContext }) { VerificationError = verificationError };
            Handler = new(
                runContext,
                Processes,
                Pages.PrepareForReplacement,
                Pages.ResumeAfterFailedHandover,
                () => Stops++,
                Logger);
        }

        public FakeShutdownExecutor Executor { get; } = new();
        public PageController Pages { get; }
        public FakeHandoverProcesses Processes { get; }
        public RecordingLogger Logger { get; } = new();
        public HandoverRequestHandler Handler { get; }
        public int Stops { get; private set; }
    }
}
