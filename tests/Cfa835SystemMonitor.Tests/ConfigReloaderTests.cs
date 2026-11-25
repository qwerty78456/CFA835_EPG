using Microsoft.Extensions.Logging;

namespace Cfa835SystemMonitor.Tests;

public sealed class ConfigReloaderTests
{
    private const string Config = @"C:\cfg\appsettings.json";
    private const string Layout = @"C:\cfg\layout.json";

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-25T08:00:00Z");

    private readonly FakeBuilder _builder = new();
    private readonly RecordingLogger _logger = new();
    private DateTimeOffset _now = Start;

    [Fact]
    public void DoesNothingWhileTheFilesMatchTheRunningConfiguration()
    {
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(1);

        for (int second = 0; second < 10; second++)
        {
            Assert.Null(Poll(reloader, afterMs: 1000));
        }

        Assert.Equal(0, _builder.Builds);
        Assert.True(_builder.Probes >= 9);
    }

    [Fact]
    public void DoesNotProbeMoreOftenThanThePollInterval()
    {
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(1);

        Poll(reloader, afterMs: 0);
        Poll(reloader, afterMs: 100);
        Poll(reloader, afterMs: 899);
        Assert.Equal(1, _builder.Probes);

        Poll(reloader, afterMs: 1);
        Assert.Equal(2, _builder.Probes);
    }

    [Fact]
    public void WaitsForTheSettleWindowBeforeBuilding()
    {
        ConfigSnapshot edited = Snapshot((Config, 2));
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => Success(edited);

        Poll(reloader, afterMs: 0);
        Assert.Null(Poll(reloader, afterMs: 1000));
        Assert.Equal(0, _builder.Builds);

        Poll(reloader, afterMs: 1000);
        Assert.Equal(1, _builder.Builds);
    }

    [Fact]
    public void RestartsTheSettleWindowWhenTheFilesKeepChanging()
    {
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        Poll(reloader, afterMs: 0);

        for (int save = 2; save < 12; save++)
        {
            _builder.Disk[Config] = Stamp(save);
            Poll(reloader, afterMs: 1000);
        }

        Assert.Equal(0, _builder.Builds);
    }

    [Fact]
    public void ReturnsTheCandidateOnceTheBuildCompletes()
    {
        ConfigSnapshot edited = Snapshot((Config, 2));
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => Success(edited);

        ConfigSnapshot? offered = DriveUntilOffered(reloader);

        Assert.Same(edited, offered);
    }

    [Fact]
    public void NeverBlocksWhileABuildIsInFlight()
    {
        ManualWork work = new();
        ConfigSnapshot edited = Snapshot((Config, 2));
        ConfigReloader reloader = new(Snapshot((Config, 1)), _builder, _logger, work);
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => Success(edited);

        Poll(reloader, afterMs: 0);
        work.RunAll();
        Poll(reloader, afterMs: 1000);
        work.RunAll();
        Poll(reloader, afterMs: 1000);
        Assert.Equal(1, work.Pending);
        int probes = _builder.Probes;

        // The build is queued but not finished: polls return at once and start no other work.
        for (int poll = 0; poll < 5; poll++)
        {
            Assert.Null(Poll(reloader, afterMs: 1000));
        }

        Assert.Equal(probes, _builder.Probes);
        Assert.Equal(1, work.Pending);

        work.RunAll();
        Assert.Same(edited, Poll(reloader, afterMs: 1));
    }

    [Fact]
    public void KeepsOfferingADeferredCandidateUntilItIsAccepted()
    {
        ConfigSnapshot edited = Snapshot((Config, 2));
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => Success(edited);
        DriveUntilOffered(reloader);

        // The shutdown dialog is open, so the loop does not accept; the offer stands.
        for (int poll = 0; poll < 5; poll++)
        {
            Assert.Same(edited, Poll(reloader, afterMs: 1000));
        }

        Assert.Equal(1, _builder.Builds);
        reloader.Accept(edited);
        Assert.Null(Poll(reloader, afterMs: 1000));
        Assert.Same(edited, reloader.Current);
    }

    [Fact]
    public void DropsADeferredCandidateWhenTheFilesChangeAgain()
    {
        ConfigSnapshot first = Snapshot((Config, 2));
        ConfigSnapshot second = Snapshot((Config, 3));
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => Success(first);
        DriveUntilOffered(reloader);

        _builder.Disk[Config] = Stamp(3);
        _builder.Build = () => Success(second);
        ConfigSnapshot? offered = null;
        for (int poll = 0; poll < 10 && offered != second; poll++)
        {
            offered = Poll(reloader, afterMs: 1000);
            Assert.True(offered is null || offered == first || offered == second);
        }

        Assert.Same(second, offered);
        Assert.Equal(2, _builder.Builds);
    }

    [Fact]
    public void AcceptMakesTheCandidateTheNewBaseline()
    {
        ConfigSnapshot edited = Snapshot((Config, 2));
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => Success(edited);
        reloader.Accept(DriveUntilOffered(reloader)!);

        for (int poll = 0; poll < 5; poll++)
        {
            Assert.Null(Poll(reloader, afterMs: 1000));
        }

        Assert.Equal(1, _builder.Builds);
    }

    [Fact]
    public void LogsOneWarningForAnInvalidEditAndKeepsTheRunningConfiguration()
    {
        ConfigSnapshot running = Snapshot((Config, 1));
        ConfigReloader reloader = Reloader(running);
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => Failure("appsettings.json: unexpected end of JSON", (Config, 2));

        for (int poll = 0; poll < 6; poll++)
        {
            Assert.Null(Poll(reloader, afterMs: 1000));
        }

        Assert.Same(running, reloader.Current);
        string warning = Assert.Single(_logger.At(LogLevel.Warning));
        Assert.Contains("unexpected end of JSON", warning, StringComparison.Ordinal);
        Assert.Contains("keeps the running configuration", warning, StringComparison.Ordinal);
        Assert.Equal(1, _builder.Builds);
    }

    [Fact]
    public void RetriesAFailedBuildOnTheSlowTimer()
    {
        // layout.json names a PNG that has not been copied in yet: nothing tracked changes when it
        // arrives, so only the retry timer can pick it up without another save.
        ConfigSnapshot fixedUp = Snapshot((Config, 2));
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => Failure("background 'art.png' was not found", (Config, 2));
        DriveUntilBuilds(reloader, 1);

        _builder.Build = () => Success(fixedUp);
        ConfigSnapshot? offered = null;
        int polls = 0;
        while (offered is null && polls++ < 30)
        {
            offered = Poll(reloader, afterMs: 1000);
        }

        Assert.Same(fixedUp, offered);
        Assert.InRange(polls, 14, 18);
    }

    [Fact]
    public void DoesNotRepeatTheWarningWhileTheSameErrorPersists()
    {
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => Failure("still broken", (Config, 2));

        for (int poll = 0; poll < 70; poll++)
        {
            Poll(reloader, afterMs: 1000);
        }

        Assert.True(_builder.Builds >= 4, $"expected periodic retries, saw {_builder.Builds}");
        Assert.Single(_logger.At(LogLevel.Warning));
    }

    [Fact]
    public void LogsAgainWhenTheErrorChanges()
    {
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => Failure("first mistake", (Config, 2));
        DriveUntilBuilds(reloader, 1);

        _builder.Disk[Config] = Stamp(3);
        _builder.Build = () => Failure("second mistake", (Config, 3));
        DriveUntilBuilds(reloader, 2);
        Poll(reloader, afterMs: 1);

        Assert.Equal(2, _logger.At(LogLevel.Warning).Count());
    }

    [Fact]
    public void RebuildsWhenAFileOnlyTheFailedCandidateReferencedChanges()
    {
        // Text mode tracks only appsettings.json. Switching it to graphic with a broken layout.json
        // fails; fixing layout.json alone must trigger a new attempt.
        ConfigSnapshot fixedUp = Snapshot((Config, 2), (Layout, 6));
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(2);
        _builder.Disk[Layout] = Stamp(5);
        _builder.Build = () => Failure("layout.json: '}' is invalid", (Config, 2), (Layout, 5));
        DriveUntilBuilds(reloader, 1);
        Poll(reloader, afterMs: 1);

        _builder.Disk[Layout] = Stamp(6);
        _builder.Build = () => Success(fixedUp);
        ConfigSnapshot? offered = null;
        for (int poll = 0; poll < 5 && offered is null; poll++)
        {
            offered = Poll(reloader, afterMs: 1000);
        }

        Assert.Same(fixedUp, offered);
        Assert.Equal(2, _builder.Builds);
    }

    [Fact]
    public void TreatsATornBuildAsStillChangingWithoutAWarning()
    {
        ConfigSnapshot edited = Snapshot((Config, 2));
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => new ConfigBuildResult(null, null, [new TrackedFile(Config, Stamp(2))], Torn: true);
        DriveUntilBuilds(reloader, 1);

        _builder.Build = () => Success(edited);
        ConfigSnapshot? offered = DriveUntilOffered(reloader);

        Assert.Same(edited, offered);
        Assert.Empty(_logger.At(LogLevel.Warning));
        Assert.Equal(2, _builder.Builds);
    }

    [Fact]
    public void StopsPollingAfterAnAppliedSnapshotDisablesReload()
    {
        ConfigSnapshot disabled = Snapshot(reloadEnabled: false, (Config, 2));
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => Success(disabled);
        reloader.Accept(DriveUntilOffered(reloader)!);
        int probes = _builder.Probes;

        _builder.Disk[Config] = Stamp(3);
        for (int poll = 0; poll < 10; poll++)
        {
            Assert.Null(Poll(reloader, afterMs: 1000));
        }

        Assert.Equal(probes, _builder.Probes);
        Assert.False(reloader.Enabled);
        Assert.Contains(_logger.At(LogLevel.Information), message => message.Contains("reload.enabled = false", StringComparison.Ordinal));
    }

    [Fact]
    public void ADisabledStartupConfigurationNeverProbes()
    {
        ConfigReloader reloader = Reloader(Snapshot(reloadEnabled: false, (Config, 1)));
        _builder.Disk[Config] = Stamp(2);

        for (int poll = 0; poll < 5; poll++)
        {
            Assert.Null(Poll(reloader, afterMs: 1000));
        }

        Assert.Equal(0, _builder.Probes);
    }

    [Fact]
    public void PollNeverThrowsWhenTheBuilderFaults()
    {
        ConfigReloader reloader = Reloader(Snapshot((Config, 1)));
        _builder.Disk[Config] = Stamp(2);
        _builder.Build = () => throw new InvalidOperationException("builder bug");

        for (int poll = 0; poll < 5; poll++)
        {
            Assert.Null(Poll(reloader, afterMs: 1000));
        }

        Assert.Contains(_logger.At(LogLevel.Warning), message => message.Contains("builder bug", StringComparison.Ordinal));
    }

    [Fact]
    public void PollNeverThrowsWhenTheBackgroundWorkFaults()
    {
        ConfigReloader reloader = new(Snapshot((Config, 1)), _builder, _logger, new ThrowingWork());

        Assert.Null(reloader.Poll(Start));
        Assert.Null(reloader.Poll(Start.AddSeconds(5)));
        Assert.NotEmpty(_logger.At(LogLevel.Warning));
    }

    private ConfigReloader Reloader(ConfigSnapshot current) => new(current, _builder, _logger, new InlineWork());

    private ConfigSnapshot? Poll(ConfigReloader reloader, int afterMs)
    {
        _now = _now.AddMilliseconds(afterMs);
        return reloader.Poll(_now);
    }

    private ConfigSnapshot? DriveUntilOffered(ConfigReloader reloader)
    {
        for (int poll = 0; poll < 20; poll++)
        {
            ConfigSnapshot? offered = Poll(reloader, afterMs: 1000);
            if (offered is not null)
            {
                return offered;
            }
        }

        return null;
    }

    private void DriveUntilBuilds(ConfigReloader reloader, int builds)
    {
        for (int poll = 0; poll < 20 && _builder.Builds < builds; poll++)
        {
            Poll(reloader, afterMs: 1000);
        }

        Assert.Equal(builds, _builder.Builds);
    }

    private static FileStamp Stamp(int version) =>
        new(true, new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc).AddSeconds(version), 100 + version);

    private static ConfigSnapshot Snapshot(params (string Path, int Version)[] files) => Snapshot(true, files);

    private static ConfigSnapshot Snapshot(bool reloadEnabled, params (string Path, int Version)[] files) => new(
        new MonitorOptions { Reload = new ReloadOptions { Enabled = reloadEnabled } },
        null,
        files.Select(file => new TrackedFile(file.Path, Stamp(file.Version))).ToArray());

    private static ConfigBuildResult Success(ConfigSnapshot snapshot) => new(snapshot, null, snapshot.Files, Torn: false);

    private static ConfigBuildResult Failure(string error, params (string Path, int Version)[] files) => new(
        null, error, files.Select(file => new TrackedFile(file.Path, Stamp(file.Version))).ToArray(), Torn: false);

    private sealed class FakeBuilder : IConfigSnapshotBuilder
    {
        public Dictionary<string, FileStamp> Disk { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Func<ConfigBuildResult> Build { get; set; } = () => throw new InvalidOperationException("no build scripted");
        public int Probes { get; private set; }
        public int Builds { get; private set; }

        public IReadOnlyList<FileStamp> Probe(IReadOnlyList<string> paths)
        {
            Probes++;
            return paths.Select(path => Disk.TryGetValue(path, out FileStamp stamp) ? stamp : default).ToArray();
        }

        public ConfigBuildResult TryBuild()
        {
            Builds++;
            return Build();
        }
    }

    /// <summary>Completes work synchronously, so a task is finished by the next poll.</summary>
    private sealed class InlineWork : IBackgroundWork
    {
        public Task<T> Run<T>(Func<T> work)
        {
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception exception)
            {
                return Task.FromException<T>(exception);
            }
        }
    }

    /// <summary>Holds work until the test runs it, to observe the reloader while work is in flight.</summary>
    private sealed class ManualWork : IBackgroundWork
    {
        private readonly Queue<Action> _queue = new();

        public int Pending => _queue.Count;

        public Task<T> Run<T>(Func<T> work)
        {
            TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(() =>
            {
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            });
            return completion.Task;
        }

        public void RunAll()
        {
            while (_queue.Count > 0)
            {
                _queue.Dequeue()();
            }
        }
    }

    private sealed class ThrowingWork : IBackgroundWork
    {
        public Task<T> Run<T>(Func<T> work) => throw new InvalidOperationException("scheduler down");
    }
}
