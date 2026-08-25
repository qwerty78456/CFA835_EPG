namespace Cfa835SystemMonitor.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void LayoutPreviewDefaultsToScaleFourAndNoExplicitPath()
    {
        CommandLineOptions options = CommandLineOptions.Parse(["--layout-preview"]);

        Assert.Equal(AppMode.LayoutPreview, options.Mode);
        Assert.Null(options.PreviewPath);
        Assert.Null(options.PreviewPage);
        Assert.Equal(4, options.PreviewScale);
    }

    [Fact]
    public void LayoutPreviewTakesAnOptionalPathWithoutSwallowingTheNextFlag()
    {
        CommandLineOptions withPath = CommandLineOptions.Parse(["--layout-preview", "out.png", "--preview-scale", "6"]);
        Assert.Equal("out.png", withPath.PreviewPath);
        Assert.Equal(6, withPath.PreviewScale);

        CommandLineOptions withoutPath = CommandLineOptions.Parse(["--layout-preview", "--preview-page", "Network"]);
        Assert.Null(withoutPath.PreviewPath);
        Assert.Equal("Network", withoutPath.PreviewPage);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("17")]
    [InlineData("six")]
    public void PreviewScaleIsRangeChecked(string value)
    {
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--layout-preview", "--preview-scale", value]));
    }

    [Fact]
    public void PreviewFlagsRequireLayoutPreview()
    {
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--preview-page", "Network"]));
    }

    [Fact]
    public void SimulateIsAcceptedWithLayoutPreview()
    {
        // A workstation without PawnIO renders cpu.temperature as "N/A", which hides whether a real
        // reading fits its box; simulating one is how the layout gets checked before deployment.
        CommandLineOptions options = CommandLineOptions.Parse(["--layout-preview", "--simulate", "thermal-90"]);

        Assert.Equal(AppMode.LayoutPreview, options.Mode);
        Assert.Equal("thermal-90", options.Simulation);
    }

    [Fact]
    public void SimulateStillRejectsTheHardwareModes()
    {
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--diagnose", "--simulate", "disk"]));
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--hardware-test", "--simulate", "disk"]));
    }

    [Fact]
    public void ListSensorsIsItsOwnMode()
    {
        CommandLineOptions options = CommandLineOptions.Parse(["--list-sensors"]);

        Assert.Equal(AppMode.ListSensors, options.Mode);
        Assert.Null(options.Simulation);
    }

    [Fact]
    public void ListSensorsRejectsSimulateBecauseItReportsRealHardware()
    {
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--list-sensors", "--simulate", "thermal-90"]));
    }

    [Fact]
    public void UnknownArgumentsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--nope"]));
    }

    [Fact]
    public void HelpTextMentionsEveryMode()
    {
        Assert.Throws<HelpRequestedException>(() => CommandLineOptions.Parse(["--help"]));

        foreach (string flag in new[]
                 { "--diagnose", "--hardware-test", "--simulate", "--layout-preview", "--list-sensors", "--config" })
        {
            Assert.Contains(flag, CommandLineOptions.HelpText, StringComparison.Ordinal);
        }
    }
}
