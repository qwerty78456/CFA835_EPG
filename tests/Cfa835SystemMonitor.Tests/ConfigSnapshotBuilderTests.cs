using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cfa835SystemMonitor.Tests;

public sealed class ConfigSnapshotBuilderTests : IDisposable
{
    // An unknown family forces the generic sans-serif fallback, which exists on every Windows box,
    // so these tests never depend on which fonts the build machine has installed.
    private const string Fonts = "\"fontFamilies\": [ \"Definitely Not An Installed Font 9471\" ]";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cfa835-snapshot-{Guid.NewGuid():N}");
    private readonly string _elsewhere = Path.Combine(Path.GetTempPath(), $"cfa835-art-{Guid.NewGuid():N}");

    public ConfigSnapshotBuilderTests()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_elsewhere);
    }

    public void Dispose()
    {
        foreach (string directory in new[] { _directory, _elsewhere })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string ConfigPath => Path.Combine(_directory, "appsettings.json");

    private string LayoutPath => Path.Combine(_directory, "layout.json");

    private void WriteConfig(string mode) =>
        File.WriteAllText(ConfigPath, $$"""{ "display": { "mode": "{{mode}}" } }""");

    private void WriteLayout(string? background = null)
    {
        string backgroundJson = background is null
            ? string.Empty
            : $"\"background\": {System.Text.Json.JsonSerializer.Serialize(background)},";
        File.WriteAllText(LayoutPath, $$"""
            {
              "version": 1,
              {{Fonts}},
              "pages": [
                {
                  "id": "Main", {{backgroundJson}}
                  "fields": [ { "source": "cpu.utilization", "format": "0.0", "x": 0, "y": 0, "width": 120, "height": 16, "sizePx": 12 } ]
                },
                { "id": "Bye", "kind": "shutdown" }
              ]
            }
            """);
    }

    private static string Png(string path, int width = 244, int height = 68)
    {
        using Bitmap bitmap = new(width, height);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Gray);
        }

        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    [Fact]
    public void BuildsATextSnapshotThatTracksOnlyTheConfigFile()
    {
        WriteConfig("text");

        ConfigBuildResult result = new ConfigSnapshotBuilder(ConfigPath).TryBuild();

        Assert.NotNull(result.Snapshot);
        Assert.Null(result.Error);
        Assert.Null(result.Snapshot.Graphic);
        Assert.Equal(DisplayMode.Text, result.Snapshot.Mode);
        TrackedFile file = Assert.Single(result.Snapshot.Files);
        Assert.Equal(ConfigPath, file.Path);
        Assert.Equal(FileStamp.Of(ConfigPath), file.Stamp);
    }

    [Fact]
    public void TextModeIgnoresABrokenLayoutFile()
    {
        // Exactly as at start-up: a text-mode configuration never reads layout.json.
        WriteConfig("text");
        File.WriteAllText(LayoutPath, "{ this is not json");

        ConfigBuildResult result = new ConfigSnapshotBuilder(ConfigPath).TryBuild();

        Assert.NotNull(result.Snapshot);
        Assert.Single(result.Files);
    }

    [Fact]
    public void BuildsAGraphicSnapshotThatTracksConfigLayoutAndBackgrounds()
    {
        WriteConfig("graphic");
        string png = Png(Path.Combine(_directory, "art.png"));
        WriteLayout("art.png");

        ConfigBuildResult result = new ConfigSnapshotBuilder(ConfigPath).TryBuild();

        Assert.Null(result.Error);
        Assert.NotNull(result.Snapshot);
        Assert.NotNull(result.Snapshot.Graphic);
        Assert.Equal(2, result.Snapshot.Graphic.Layout.Pages.Count);
        Assert.Equal([ConfigPath, LayoutPath, png], result.Snapshot.Files.Select(file => file.Path));
        Assert.All(result.Snapshot.Files, file => Assert.True(file.Stamp.Exists));
    }

    [Fact]
    public void TracksAnAbsoluteBackgroundOutsideTheConfigDirectory()
    {
        WriteConfig("graphic");
        string png = Png(Path.Combine(_elsewhere, "shared-art.png"));
        WriteLayout(png);

        ConfigBuildResult result = new ConfigSnapshotBuilder(ConfigPath).TryBuild();

        Assert.NotNull(result.Snapshot);
        Assert.Contains(result.Snapshot.Files, file => file.Path == png);
    }

    [Fact]
    public void RejectsMalformedJsonAndNamesTheFile()
    {
        File.WriteAllText(ConfigPath, """{ "display": { "mode": "graphic" """);

        ConfigBuildResult result = new ConfigSnapshotBuilder(ConfigPath).TryBuild();

        Assert.Null(result.Snapshot);
        Assert.False(result.Torn);
        Assert.NotNull(result.Error);
        Assert.Contains(ConfigPath, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAMissingConfigFileInsteadOfRevertingToDefaults()
    {
        ConfigBuildResult result = new ConfigSnapshotBuilder(ConfigPath).TryBuild();

        Assert.Null(result.Snapshot);
        Assert.NotNull(result.Error);
        Assert.Contains("was not found", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnInvalidSettingAndNamesTheFile()
    {
        File.WriteAllText(ConfigPath, """{ "sampling": { "activityMs": 1 } }""");

        ConfigBuildResult result = new ConfigSnapshotBuilder(ConfigPath).TryBuild();

        Assert.NotNull(result.Error);
        Assert.Contains(ConfigPath, result.Error, StringComparison.Ordinal);
        Assert.Contains("sampling.activityMs", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsABackgroundOfTheWrongPixelSizeAndNamesTheFile()
    {
        WriteConfig("graphic");
        string png = Png(Path.Combine(_directory, "art.png"), width: 100, height: 50);
        WriteLayout("art.png");

        ConfigBuildResult result = new ConfigSnapshotBuilder(ConfigPath).TryBuild();

        Assert.Null(result.Snapshot);
        Assert.NotNull(result.Error);
        Assert.Contains(png, result.Error, StringComparison.Ordinal);
        Assert.Contains("244x68", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsABackgroundThatIsNotAnImage()
    {
        WriteConfig("graphic");
        string png = Path.Combine(_directory, "art.png");
        File.WriteAllText(png, "half-copied file, not a PNG");
        WriteLayout("art.png");

        ConfigBuildResult result = new ConfigSnapshotBuilder(ConfigPath).TryBuild();

        Assert.Null(result.Snapshot);
        Assert.NotNull(result.Error);
        Assert.Contains(png, result.Error, StringComparison.Ordinal);
        Assert.Contains("'Main'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedBuildStillReportsTheFilesItRead()
    {
        // A text -> graphic switch whose layout is broken: the reloader must watch layout.json too,
        // or fixing it would never trigger another attempt.
        WriteConfig("graphic");
        File.WriteAllText(LayoutPath, "{ broken");

        ConfigBuildResult result = new ConfigSnapshotBuilder(ConfigPath).TryBuild();

        Assert.NotNull(result.Error);
        Assert.Contains(LayoutPath, result.Error, StringComparison.Ordinal);
        Assert.Equal([ConfigPath, LayoutPath], result.Files.Select(file => file.Path));
    }

    [Fact]
    public void ReportsATornBuildWhenAFileChangesWhileItIsRead()
    {
        WriteConfig("text");
        int calls = 0;
        ConfigSnapshotBuilder builder = new(ConfigPath, path =>
            new FileStamp(true, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++calls), 10));

        ConfigBuildResult result = builder.TryBuild();

        Assert.True(result.Torn);
        Assert.Null(result.Snapshot);
        Assert.Null(result.Error);
    }

    [Fact]
    public void AFailureWhileTheFileIsStillChangingIsTornNotAnError()
    {
        File.WriteAllText(ConfigPath, "{ half-written");
        int calls = 0;
        ConfigSnapshotBuilder builder = new(ConfigPath, path => new FileStamp(true, DateTime.UnixEpoch, ++calls));

        ConfigBuildResult result = builder.TryBuild();

        Assert.True(result.Torn);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ProbeNeverThrows()
    {
        ConfigSnapshotBuilder builder = new(ConfigPath, path => throw new IOException("network path vanished"));

        IReadOnlyList<FileStamp> stamps = builder.Probe([ConfigPath, LayoutPath]);

        Assert.Equal([default(FileStamp), default(FileStamp)], stamps);
    }

    [Fact]
    public void StartupBuildUsesTheStampTakenBeforeTheConfigWasRead()
    {
        WriteConfig("text");
        MonitorOptions options = MonitorOptions.Load(ConfigPath);

        ConfigSnapshot snapshot = new ConfigSnapshotBuilder(ConfigPath).BuildFrom(options, NullLoggerFactory.Instance);

        Assert.Equal(options.ConfigStamp, Assert.Single(snapshot.Files).Stamp);
    }

    [Fact]
    public void StartupBuildThrowsForAnInvalidBackground()
    {
        WriteConfig("graphic");
        Png(Path.Combine(_directory, "art.png"), width: 10, height: 10);
        WriteLayout("art.png");
        MonitorOptions options = MonitorOptions.Load(ConfigPath);

        Assert.Throws<InvalidDataException>(
            () => new ConfigSnapshotBuilder(ConfigPath).BuildFrom(options, NullLoggerFactory.Instance));
    }
}
