using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Cfa835SystemMonitor;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CommandLineOptions commandLine;
        try
        {
            commandLine = CommandLineOptions.Parse(args);
        }
        catch (HelpRequestedException)
        {
            Console.WriteLine(CommandLineOptions.HelpText);
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(CommandLineOptions.HelpText);
            return 64;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("CFA835 System Monitor requires Windows.");
            return 78;
        }

        MonitorOptions options;
        try
        {
            options = MonitorOptions.Load(commandLine.ConfigPath);
            if (options.Display.ResolvedMode == DisplayMode.Graphic || commandLine.Mode == AppMode.LayoutPreview)
            {
                // Parse the layout here so a bad file fails with the configuration exit code rather
                // than crashing later inside the monitor loop.
                _ = LayoutDocument.Load(options.ResolveLayoutPath());
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"Configuration error: {exception.Message}");
            return 78;
        }

        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
            });
        });

        // Intentionally lives until process teardown because ProcessExit may fire after Main returns.
        CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

        try
        {
            MonitorApplication application = new(options, loggerFactory);
            return commandLine.Mode switch
            {
                AppMode.Diagnose => await application.DiagnoseAsync(shutdown.Token),
                AppMode.HardwareTest => await application.HardwareTestAsync(commandLine.NonInteractive, shutdown.Token),
                AppMode.LayoutPreview => await application.LayoutPreviewAsync(commandLine, shutdown.Token),
                AppMode.ListSensors => await application.ListSensorsAsync(shutdown.Token),
                _ => await application.RunAsync(commandLine.Simulation, shutdown.Token)
            };
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("Fatal").LogCritical(exception, "Unrecoverable application failure");
            return 1;
        }
    }
}
