namespace Cfa835SystemMonitor.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cfa835-config-{Guid.NewGuid():N}");

    public ConfigurationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string json, string name = "appsettings.json")
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void MissingFileYieldsDefaultsAtStartup()
    {
        MonitorOptions options = MonitorOptions.Load(Path.Combine(_directory, "absent.json"));

        Assert.Equal(DisplayMode.Text, options.Display.ResolvedMode);
        Assert.False(options.ConfigStamp.Exists);
        Assert.True(options.Reload.Enabled);
    }

    [Fact]
    public void MissingFileIsAnErrorWhenAFileIsRequired()
    {
        // A live reload must never read "the file vanished mid-save" as "revert every setting".
        string path = Path.Combine(_directory, "absent.json");

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(
            () => MonitorOptions.Load(path, allowMissing: false));
        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadExposesTheResolvedAbsoluteConfigPath()
    {
        string path = Write("{}");
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, path);

        MonitorOptions options = MonitorOptions.Load(relative);

        Assert.Equal(path, options.ConfigPath);
        Assert.Equal(_directory, options.ConfigDirectory);
    }

    [Fact]
    public void LoadCapturesTheStampOfTheFileItRead()
    {
        string path = Write("{}");
        DateTime written = new(2026, 11, 1, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        MonitorOptions options = MonitorOptions.Load(path);

        Assert.True(options.ConfigStamp.Exists);
        Assert.Equal(written, options.ConfigStamp.LastWriteTimeUtc);
        Assert.Equal(new FileInfo(path).Length, options.ConfigStamp.Length);
        Assert.Equal(FileStamp.Of(path), options.ConfigStamp);
    }

    [Fact]
    public void ReloadGroupDefaultsToEnabledWithOneSecondPolling()
    {
        // Existing installs have no "reload" group; upgrading turns live reload on.
        MonitorOptions options = MonitorOptions.Load(Write("""{ "display": { "mode": "text" } }"""));

        Assert.True(options.Reload.Enabled);
        Assert.Equal(1000, options.Reload.PollMs);
    }

    [Fact]
    public void ReloadCanBeSwitchedOff()
    {
        MonitorOptions options = MonitorOptions.Load(Write("""{ "reload": { "enabled": false } }"""));

        Assert.False(options.Reload.Enabled);
    }

    [Theory]
    [InlineData(249)]
    [InlineData(60_001)]
    public void RejectsReloadPollIntervalOutsideItsRange(int pollMs)
    {
        string path = Write($$"""{ "reload": { "pollMs": {{pollMs}} } }""");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => MonitorOptions.Load(path));
        Assert.Contains("reload.pollMs", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("sampling")]
    [InlineData("display")]
    [InlineData("thermal")]
    [InlineData("shutdown")]
    [InlineData("reload")]
    public void RejectsANullOptionGroup(string group)
    {
        // Previously a NullReferenceException that escaped the exit-78 configuration error mapping.
        string path = Write($$"""{ "{{group}}": null }""");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => MonitorOptions.Load(path));
        Assert.Contains("must not be null", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionGroupsCompareByValue()
    {
        string path = Write("""
            {
              "sampling": { "temperatureMs": 2000 },
              "display": { "mode": "graphic", "autoCycleSeconds": 9 },
              "thermal": { "tjMaxC": 95 }
            }
            """);

        MonitorOptions first = MonitorOptions.Load(path);
        MonitorOptions second = MonitorOptions.Load(path);

        Assert.Equal(first.Device, second.Device);
        Assert.Equal(first.Sampling, second.Sampling);
        Assert.Equal(first.Display, second.Display);
        Assert.Equal(first.Thermal, second.Thermal);
        Assert.Equal(first.Shutdown, second.Shutdown);
        Assert.Equal(first.Reload, second.Reload);
        Assert.NotEqual(first.Thermal, first.Thermal with { TjMaxC = 96 });
    }

    [Fact]
    public void RelativeLayoutPathResolvesBesideTheConfigFile()
    {
        MonitorOptions options = MonitorOptions.Load(Write("""{ "display": { "layoutPath": "panel\\layout.json" } }"""));

        Assert.Equal(Path.Combine(_directory, "panel", "layout.json"), options.ResolveLayoutPath());
    }

    [Fact]
    public void ReadsAFileThatAnEditorHoldsOpenForWriting()
    {
        // File.ReadAllText denies writers, so reading while an editor had the file open used to fail
        // (and, worse, could make the editor's own save fail while the monitor was reading).
        string path = Write("""{ "shutdown": { "countdownSeconds": 45 } }""");
        using FileStream editor = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        MonitorOptions options = MonitorOptions.Load(path);

        Assert.Equal(45, options.Shutdown.CountdownSeconds);
    }

    [Fact]
    public void FileStampOfAMissingFileIsTheDefault()
    {
        Assert.Equal(default, FileStamp.Of(Path.Combine(_directory, "absent.png")));
    }

    [Fact]
    public void FileStampNeverThrowsForAnInvalidPath()
    {
        Assert.False(FileStamp.Of("C:\\bad\0path\\x.json").Exists);
    }
}
