using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cfa835SystemMonitor;

/// <summary>
/// What the reloader compares to decide that a configuration file changed. Existence, last write
/// time and length together catch every save an editor performs (truncate-and-write, temp-file
/// rename, copy-over) without reading the file.
/// </summary>
public readonly record struct FileStamp(bool Exists, DateTime LastWriteTimeUtc, long Length)
{
    /// <summary>
    /// Stats <paramref name="path"/>, following a symbolic link to its final target (a link's own
    /// time and length never change when the target is edited). Never throws: an unreadable path or
    /// a broken link stamps as missing.
    /// </summary>
    public static FileStamp Of(string path)
    {
        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
            {
                return default;
            }

            if (info.LinkTarget is not null)
            {
                return info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo { Exists: true } target
                    ? new FileStamp(true, target.LastWriteTimeUtc, target.Length)
                    : default;
            }

            return new FileStamp(true, info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return default;
        }
    }
}

/// <summary>One file a configuration snapshot was built from, stamped before it was read.</summary>
public sealed record TrackedFile(string Path, FileStamp Stamp);

public static class SharedFile
{
    /// <summary>
    /// Reads a text file while letting other processes keep writing, renaming or deleting it.
    /// <see cref="File.ReadAllText(string)"/> denies writers for the duration of the read, which
    /// would make an operator's editor fail to save whenever the monitor happens to be reading.
    /// </summary>
    public static string ReadAllText(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// One fully validated configuration: options, plus (in graphic mode) a runtime whose glyph atlas
/// is rasterized, backgrounds decoded and every page proven to render. Immutable, so the render
/// loop can switch to it with a single reference assignment.
/// </summary>
public sealed class ConfigSnapshot(MonitorOptions options, GraphicRuntime? graphic, IReadOnlyList<TrackedFile> files)
{
    public MonitorOptions Options { get; } = options;
    public GraphicRuntime? Graphic { get; } = graphic;

    /// <summary>The config file; in graphic mode also the layout and every page background.</summary>
    public IReadOnlyList<TrackedFile> Files { get; } = files;

    public DisplayMode Mode => Options.Display.ResolvedMode;
}

/// <param name="Snapshot">The new configuration, when every file was valid.</param>
/// <param name="Error">Why the edit was rejected, naming the file; null on success or when torn.</param>
/// <param name="Files">Every file the attempt read, valid or not, so a fix to any of them is noticed.</param>
/// <param name="Torn">A file changed while it was being read: still being saved, not an error.</param>
public sealed record ConfigBuildResult(
    ConfigSnapshot? Snapshot,
    string? Error,
    IReadOnlyList<TrackedFile> Files,
    bool Torn);

/// <summary>The disk-touching half of live reload. Both members run off the render loop and never throw.</summary>
public interface IConfigSnapshotBuilder
{
    IReadOnlyList<FileStamp> Probe(IReadOnlyList<string> paths);

    ConfigBuildResult TryBuild();
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ConfigSnapshotBuilder(string configPath, Func<string, FileStamp>? stamp = null) : IConfigSnapshotBuilder
{
    private readonly Func<string, FileStamp> _stamp = stamp ?? FileStamp.Of;

    public string ConfigPath { get; } = configPath;

    public IReadOnlyList<FileStamp> Probe(IReadOnlyList<string> paths)
    {
        FileStamp[] stamps = new FileStamp[paths.Count];
        for (int index = 0; index < paths.Count; index++)
        {
            stamps[index] = SafeStamp(paths[index]);
        }

        return stamps;
    }

    /// <summary>
    /// Builds the start-up snapshot from options <see cref="Program"/> already loaded and validated.
    /// Unlike <see cref="TryBuild"/> it logs the layout/atlas/background lines as before and throws
    /// on failure, exactly as start-up always has.
    /// </summary>
    public ConfigSnapshot BuildFrom(MonitorOptions options, ILoggerFactory loggerFactory)
    {
        List<TrackedFile> files = [new TrackedFile(ConfigPath, options.ConfigStamp)];
        GraphicRuntime? graphic = null;
        if (options.Display.ResolvedMode == DisplayMode.Graphic)
        {
            graphic = BuildGraphic(options, files, loggerFactory);
        }

        return new ConfigSnapshot(options, graphic, files);
    }

    public ConfigBuildResult TryBuild()
    {
        List<TrackedFile> files = [];
        string stage = ConfigPath;
        try
        {
            files.Add(new TrackedFile(ConfigPath, SafeStamp(ConfigPath)));
            MonitorOptions options = MonitorOptions.Load(ConfigPath, allowMissing: false);

            GraphicRuntime? graphic = null;
            if (options.Display.ResolvedMode == DisplayMode.Graphic)
            {
                // A text-mode config never reads layout.json, so a broken or absent layout is legal
                // there -- exactly as at start-up (Program only validates it in graphic mode).
                stage = options.ResolveLayoutPath();
                graphic = BuildGraphic(options, files, NullLoggerFactory.Instance);
            }

            return IsTorn(files)
                ? new ConfigBuildResult(null, null, files, Torn: true)
                : new ConfigBuildResult(new ConfigSnapshot(options, graphic, files), null, files, Torn: false);
        }
        catch (Exception exception)
        {
            // Everything is caught: "sampling": null, a truncated PNG (GDI+ ArgumentException), a
            // sharing violation (UnauthorizedAccessException) and anything else an operator's
            // half-saved edit can produce must reach the operator as a log line, not a crash.
            return IsTorn(files)
                ? new ConfigBuildResult(null, null, files, Torn: true)
                : new ConfigBuildResult(null, Describe(stage, exception), files, Torn: false);
        }
    }

    private GraphicRuntime BuildGraphic(MonitorOptions options, List<TrackedFile> files, ILoggerFactory loggerFactory)
    {
        string layoutPath = options.ResolveLayoutPath();
        files.Add(new TrackedFile(layoutPath, SafeStamp(layoutPath)));
        LayoutDocument layout = LayoutDocument.Load(layoutPath);

        // Stamped after the layout names them but before GraphicRuntime decodes them.
        foreach (string background in layout.Pages
            .Select(page => page.BackgroundPath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            files.Add(new TrackedFile(Path.GetFullPath(background), SafeStamp(background)));
        }

        GraphicRuntime graphic = GraphicRuntime.Create(layout, layoutPath, loggerFactory);
        graphic.VerifyRenders();
        return graphic;
    }

    private bool IsTorn(IEnumerable<TrackedFile> files) => files.Any(file => SafeStamp(file.Path) != file.Stamp);

    private FileStamp SafeStamp(string path)
    {
        try
        {
            return _stamp(path);
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static string Describe(string stage, Exception exception)
    {
        string message = exception.Message;
        return message.Contains(stage, StringComparison.OrdinalIgnoreCase) ? message : $"{stage}: {message}";
    }
}

/// <summary>Runs reload work off the render loop. Injected so tests control exactly when work completes.</summary>
public interface IBackgroundWork
{
    Task<T> Run<T>(Func<T> work);
}

public sealed class ThreadPoolWork : IBackgroundWork
{
    public static readonly ThreadPoolWork Instance = new();

    public Task<T> Run<T>(Func<T> work) => Task.Run(work);
}

/// <summary>
/// Decides when to rebuild the configuration and hands the render loop a finished snapshot. All
/// disk access (stamping, parsing, rasterizing, decoding) runs as background work; <see cref="Poll"/>
/// only inspects completed tasks, so it never blocks, never touches the disk and never throws.
/// </summary>
/// <remarks>
/// Lifecycle of an edit: a probe sees stamps that differ from the expected ones, which starts the
/// settle window; once a probe sees the same stamps for <see cref="SettleWindow"/> a single build
/// runs. A valid result is offered on every poll until <see cref="Accept"/> (the controller may
/// defer it while the shutdown dialog is open) and is dropped if the files move again meanwhile. An
/// invalid result is logged once and keeps the running configuration; both the running files and
/// every file the failed attempt read are then watched, and the build is retried every
/// <see cref="FailedRetryInterval"/> so a file that did not exist yet (a background PNG referenced
/// before it is copied in) is picked up without another save.
/// </remarks>
public sealed class ConfigReloader
{
    public static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan FailedRetryInterval = TimeSpan.FromSeconds(15);

    private readonly IConfigSnapshotBuilder _builder;
    private readonly IBackgroundWork _work;
    private readonly ILogger _logger;

    private ConfigSnapshot _current;
    private IReadOnlyList<TrackedFile> _expected;
    private DateTimeOffset _nextProbe = DateTimeOffset.MinValue;
    private Task<IReadOnlyList<FileStamp>>? _probe;
    private IReadOnlyList<string>? _probePaths;
    private Task<ConfigBuildResult>? _build;
    private IReadOnlyList<TrackedFile>? _buildBaseline;
    private IReadOnlyList<FileStamp>? _settlingOn;
    private DateTimeOffset _settleStart;
    private ConfigSnapshot? _candidate;
    private string? _failure;
    private string? _loggedFailure;
    private DateTimeOffset _retryAt = DateTimeOffset.MaxValue;
    private bool _disabledLogged;

    public ConfigReloader(ConfigSnapshot current, IConfigSnapshotBuilder builder, ILogger logger, IBackgroundWork? work = null)
    {
        _current = current;
        _expected = current.Files;
        _builder = builder;
        _logger = logger;
        _work = work ?? ThreadPoolWork.Instance;
    }

    public ConfigSnapshot Current => _current;

    /// <summary>The last rejection message, or null when the newest attempt succeeded.</summary>
    public string? Failure => _failure;

    public bool Enabled => _current.Options.Reload.Enabled;

    /// <summary>A validated configuration ready to apply, or null. Never throws, blocks or touches the disk.</summary>
    /// <param name="now">
    /// A monotonic time. Only differences between calls matter; a wall clock stepped backwards
    /// would otherwise suspend probing, settling and retries for the size of the step.
    /// </param>
    public ConfigSnapshot? Poll(DateTimeOffset now)
    {
        try
        {
            return PollCore(now);
        }
        catch (Exception exception)
        {
            // Defensive: nothing here should throw, and the render loop must not see it if it does.
            _logger.LogWarning(exception, "Configuration reload check failed; will retry");
            _probe = null;
            _build = null;
            _nextProbe = now.AddMilliseconds(_current.Options.Reload.PollMs);
            return null;
        }
    }

    /// <summary>Makes an applied snapshot the new baseline.</summary>
    public void Accept(ConfigSnapshot applied)
    {
        _current = applied;
        _expected = applied.Files;
        _candidate = null;
        _failure = null;
        _loggedFailure = null;
        _retryAt = DateTimeOffset.MaxValue;
        _settlingOn = null;
        if (!applied.Options.Reload.Enabled && !_disabledLogged)
        {
            _disabledLogged = true;
            _logger.LogInformation(
                "Live configuration reload is now disabled (reload.enabled = false); restart the monitor to apply further edits");
        }
    }

    private ConfigSnapshot? PollCore(DateTimeOffset now)
    {
        if (!_current.Options.Reload.Enabled)
        {
            return null;
        }

        if (_build is not null)
        {
            if (!_build.IsCompleted)
            {
                return null;
            }

            ConfigBuildResult result = _build.IsCompletedSuccessfully
                ? _build.Result
                : new ConfigBuildResult(
                    null,
                    $"{_current.Options.ConfigPath}: {_build.Exception?.GetBaseException().Message ?? "reload was cancelled"}",
                    [],
                    Torn: false);
            _build = null;
            OnBuilt(result, now);
        }

        if (_probe is { IsCompleted: true })
        {
            IReadOnlyList<FileStamp>? stamps = _probe.IsCompletedSuccessfully ? _probe.Result : null;
            IReadOnlyList<string> paths = _probePaths ?? [];
            _probe = null;
            _probePaths = null;
            if (stamps is not null)
            {
                OnProbed(paths, stamps, now);
            }
        }

        if (_build is null && _probe is null && now >= _nextProbe)
        {
            IReadOnlyList<TrackedFile> watched = Watched();
            string[] paths = watched.Select(file => file.Path).ToArray();
            _probePaths = paths;
            _probe = _work.Run(() => _builder.Probe(paths));
            _nextProbe = now.AddMilliseconds(_current.Options.Reload.PollMs);
        }

        return _candidate;
    }

    // The stamps the watched files should have if nothing has changed since the newest snapshot we
    // know about: a held candidate's, or else the running configuration's.
    private IReadOnlyList<TrackedFile> Watched() => _candidate?.Files ?? _expected;

    private void OnProbed(IReadOnlyList<string> paths, IReadOnlyList<FileStamp> stamps, DateTimeOffset now)
    {
        IReadOnlyList<TrackedFile> watched = Watched();
        bool unchanged = paths.Count == watched.Count && stamps.Count == watched.Count;
        for (int index = 0; unchanged && index < watched.Count; index++)
        {
            unchanged = string.Equals(paths[index], watched[index].Path, StringComparison.OrdinalIgnoreCase)
                && stamps[index] == watched[index].Stamp;
        }

        if (unchanged)
        {
            _settlingOn = null;
            if (_failure is not null && now >= _retryAt)
            {
                StartBuild(paths, stamps);
            }

            return;
        }

        if (_candidate is not null)
        {
            // Edited again while the candidate waited (e.g. behind the shutdown dialog): it no longer
            // matches the disk, so rebuild from what is there now. Its stamps join the baseline so
            // the file that moved still differs on the next probe. Falling back to the running (or
            // last failed) watch list alone could already match the disk -- a candidate built by the
            // slow retry, for instance -- and the edit would then never be rebuilt.
            _expected = MergeWatchList(_expected, _candidate.Files);
            _candidate = null;
            _settlingOn = null;
        }

        if (_settlingOn is null || !_settlingOn.SequenceEqual(stamps))
        {
            _settlingOn = stamps;
            _settleStart = now;
            return;
        }

        if (now - _settleStart >= SettleWindow)
        {
            StartBuild(paths, stamps);
        }
    }

    private void StartBuild(IReadOnlyList<string> paths, IReadOnlyList<FileStamp> stamps)
    {
        _settlingOn = null;
        _buildBaseline = paths.Zip(stamps, (path, stamp) => new TrackedFile(path, stamp)).ToArray();
        _build = _work.Run(_builder.TryBuild);
    }

    private void OnBuilt(ConfigBuildResult result, DateTimeOffset now)
    {
        IReadOnlyList<TrackedFile> baseline = _buildBaseline ?? [];
        _buildBaseline = null;
        if (result.Torn)
        {
            // Still being written. The next probe will see moving stamps and settle again.
            _settlingOn = null;
            return;
        }

        if (result.Snapshot is not null)
        {
            _candidate = result.Snapshot;
            _failure = null;
            _retryAt = DateTimeOffset.MaxValue;

            // A valid candidate has been announced, so the next rejection must be logged even if its
            // text repeats an earlier one; otherwise the log would still promise the deferred change.
            _loggedFailure = null;
            return;
        }

        _failure = result.Error ?? "unknown configuration error";
        _retryAt = now + FailedRetryInterval;

        // Watched files the failed attempt never reached (a background behind a layout typo, a
        // layout.json a graphic -> text switch stopped reading) take the stamps that were on disk
        // when the build started. Keeping their older running stamps would make the next probe see
        // a "change" and rebuild every second instead of every FailedRetryInterval.
        _expected = MergeWatchList(MergeWatchList(_current.Files, baseline), result.Files);
        if (!string.Equals(_failure, _loggedFailure, StringComparison.Ordinal))
        {
            _loggedFailure = _failure;
            _logger.LogWarning(
                "Configuration change rejected; the display keeps the running configuration. {Error}", _failure);
        }
    }

    // The running configuration's files plus whatever the failed attempt read, with the failed
    // attempt's stamps taking precedence (they are newer). Watching the union means a fix to a file
    // only the failed attempt knew about -- a layout.json that a mode switch pulled in -- is noticed.
    private static IReadOnlyList<TrackedFile> MergeWatchList(IReadOnlyList<TrackedFile> running, IReadOnlyList<TrackedFile> attempted)
    {
        List<TrackedFile> merged = [];
        Dictionary<string, int> positions = new(StringComparer.OrdinalIgnoreCase);
        foreach (TrackedFile file in running.Concat(attempted))
        {
            if (positions.TryGetValue(file.Path, out int position))
            {
                merged[position] = file;
            }
            else
            {
                positions[file.Path] = merged.Count;
                merged.Add(file);
            }
        }

        return merged;
    }
}
