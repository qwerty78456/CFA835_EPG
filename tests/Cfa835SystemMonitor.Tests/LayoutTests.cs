namespace Cfa835SystemMonitor.Tests;

public sealed class LayoutTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cfa835-layout-{Guid.NewGuid():N}");

    public LayoutTests() => Directory.CreateDirectory(_directory);

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

    private LayoutDocument Load(string json)
    {
        string path = Path.Combine(_directory, "layout.json");
        File.WriteAllText(path, json);
        return LayoutDocument.Load(path);
    }

    private const string MinimalPage = """
        {
          "version": 1,
          "pages": [
            {
              "id": "DateTime",
              "fields": [
                { "source": "datetime", "format": "HH:mm:ss", "x": 64, "y": 4, "width": 176, "height": 18,
                  "align": "center", "sizePx": 24 }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void ParsesFieldsAndResolvesEnums()
    {
        LayoutDocument layout = Load(MinimalPage);

        LayoutPage page = Assert.Single(layout.Pages);
        LayoutField field = Assert.Single(page.Fields);
        Assert.Equal(LayoutPageKind.Normal, page.ResolvedKind);
        Assert.Equal(LayoutFieldSource.DateTime, field.ResolvedSource);
        Assert.Equal(LayoutAlign.Center, field.ResolvedAlign);
        Assert.Equal(250, layout.RefreshMs);
        Assert.Null(page.BackgroundPath);
    }

    [Theory]
    [InlineData("\"x\": 200, \"y\": 4, \"width\": 100, \"height\": 18")]
    [InlineData("\"x\": 0, \"y\": 60, \"width\": 40, \"height\": 18")]
    [InlineData("\"x\": -1, \"y\": 0, \"width\": 40, \"height\": 18")]
    public void RejectsFieldsThatLeaveTheDisplay(string rectangle)
    {
        string json = $$"""
            {
              "version": 1,
              "pages": [
                { "id": "DateTime", "fields": [ { "source": "datetime", {{rectangle}}, "sizePx": 12 } ] }
              ]
            }
            """;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Load(json));
        Assert.Contains("does not fit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnknownSourceAndListsTheValidOnes()
    {
        string json = MinimalPage.Replace("\"source\": \"datetime\"", "\"source\": \"cpu.voltage\"", StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Load(json));
        Assert.Contains("cpu.voltage", error.Message, StringComparison.Ordinal);
        Assert.Contains("cpu.utilization", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAMalformedDateFormatString()
    {
        // Format validation is best effort. .NET turns stray characters in a date format into
        // literals and is even more permissive with custom numeric formats, so an unterminated
        // quoted literal is about the only thing that can be caught before the field is drawn.
        string json = MinimalPage.Replace("\"format\": \"HH:mm:ss\"", "\"format\": \"HH:mm'\"", StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Load(json));
        Assert.Contains("is not valid for source", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"refreshMs\": 50", "refreshMs")]
    [InlineData("\"version\": 2", "version")]
    [InlineData("\"fontFamilies\": []", "fontFamilies")]
    public void RejectsDocumentLevelValuesOutsideTheirRange(string overrideJson, string expected)
    {
        string json = MinimalPage.Replace("\"version\": 1,", $"\"version\": 1, {overrideJson},", StringComparison.Ordinal);
        if (expected == "version")
        {
            json = MinimalPage.Replace("\"version\": 1", overrideJson, StringComparison.Ordinal);
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Load(json));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsABackgroundThatIsNotOnDisk()
    {
        string json = MinimalPage.Replace(
            "\"id\": \"DateTime\",", "\"id\": \"DateTime\", \"background\": \"missing.png\",", StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Load(json));
        Assert.Contains("missing.png", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicatePageIds()
    {
        string json = """
            {
              "version": 1,
              "pages": [
                { "id": "Main", "fields": [ { "source": "autocycle", "x": 0, "y": 0, "width": 40, "height": 12 } ] },
                { "id": "main", "fields": [ { "source": "autocycle", "x": 0, "y": 0, "width": 40, "height": 12 } ] }
              ]
            }
            """;

        Assert.Throws<InvalidDataException>(() => Load(json));
    }

    [Fact]
    public void ShutdownPageFallsBackToTheBuiltInTemplates()
    {
        string json = """
            {
              "version": 1,
              "pages": [
                { "id": "Main", "fields": [ { "source": "autocycle", "x": 0, "y": 0, "width": 40, "height": 12 } ] },
                { "id": "Shutdown", "kind": "shutdown", "fields": [] }
              ]
            }
            """;

        LayoutDocument layout = Load(json);
        LayoutPage shutdown = layout.Pages[1];

        Assert.Equal(LayoutPageKind.Shutdown, shutdown.ResolvedKind);
        foreach (ShutdownUiState state in Enum.GetValues<ShutdownUiState>())
        {
            Assert.NotEmpty(shutdown.FieldsFor(state));
        }

        Assert.Contains(
            shutdown.FieldsFor(ShutdownUiState.CountingDown),
            field => field.ResolvedSource == LayoutFieldSource.ShutdownRemainingSeconds);
    }

    [Fact]
    public void ShutdownStateOverrideWinsOverTheTemplate()
    {
        string json = """
            {
              "version": 1,
              "pages": [
                { "id": "Main", "fields": [ { "source": "autocycle", "x": 0, "y": 0, "width": 40, "height": 12 } ] },
                {
                  "id": "Shutdown", "kind": "shutdown", "fields": [],
                  "states": {
                    "idle": [ { "source": "literal", "text": "BYE", "x": 0, "y": 0, "width": 244, "height": 20,
                                "align": "center", "sizePx": 16 } ]
                  }
                }
              ]
            }
            """;

        LayoutPage shutdown = Load(json).Pages[1];

        Assert.Equal("BYE", Assert.Single(shutdown.FieldsFor(ShutdownUiState.Idle)).Text);
        // Untouched sub-states keep the built-in wording.
        Assert.True(shutdown.FieldsFor(ShutdownUiState.Confirm).Count > 1);
    }

    [Fact]
    public void DescriptorsFlagShutdownPagesForTheNavigationRing()
    {
        string json = """
            {
              "version": 1,
              "pages": [
                { "id": "Main", "fields": [ { "source": "autocycle", "x": 0, "y": 0, "width": 40, "height": 12 } ] },
                { "id": "Extra", "fields": [ { "source": "net.rx", "x": 0, "y": 0, "width": 40, "height": 12 } ] },
                { "id": "Shutdown", "kind": "shutdown", "fields": [] }
              ]
            }
            """;

        IReadOnlyList<PageDescriptor> descriptors = Load(json).Descriptors();

        Assert.Equal(["Main", "Extra", "Shutdown"], descriptors.Select(item => item.Id));
        Assert.Equal([false, false, true], descriptors.Select(item => item.IsShutdown));
    }

    [Fact]
    public void RejectsALayoutMadeEntirelyOfShutdownPages()
    {
        string json = """
            { "version": 1, "pages": [ { "id": "Shutdown", "kind": "shutdown", "fields": [] } ] }
            """;

        Assert.Throws<InvalidDataException>(() => Load(json));
    }

    [Fact]
    public void MissingFileIsAConfigurationErrorNotACrash()
    {
        Assert.Throws<InvalidDataException>(() => LayoutDocument.Load(Path.Combine(_directory, "absent.json")));
    }
}
