using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Cfa835SystemMonitor;

public static class Program
{
    private const int PermissionDeniedExitCode = 77;
    private const int OwnershipConflictExitCode = 75;

    public static int Main(string[] args)
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

        if (!WindowsSecurity.IsElevatedAdministrator())
        {
            Console.Error.WriteLine(
                "CFA835 System Monitor requires an elevated Administrator token. Re-run it with Run as administrator.");
            return PermissionDeniedExitCode;
        }

        MonitorOptions options;
        try
        {
            options = MonitorOptions.Load(commandLine.ConfigPath);
            if (options.Display.ResolvedMode == DisplayMode.Graphic || commandLine.Mode == AppMode.LayoutPreview)
            {
                // Validate before instance replacement so a bad new configuration never stops a
                // healthy process that is already driving the display.
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
            if (!InstanceCoordinator.UsesCfaDevice(commandLine.Mode))
            {
                return RunApplicationAsync(application, commandLine, shutdown.Token).GetAwaiter().GetResult();
            }

            using InstanceCoordinator coordinator = new(
                commandLine.Mode,
                loggerFactory.CreateLogger<InstanceCoordinator>());
            InstanceAcquireResult ownership = coordinator.Acquire();
            if (!ownership.Acquired)
            {
                loggerFactory.CreateLogger("Instance").LogError("{Error}", ownership.Error);
                return OwnershipConflictExitCode;
            }

            coordinator.StartControlServer(application.PrepareForReplacement, shutdown.Cancel);

            return RunApplicationAsync(application, commandLine, shutdown.Token).GetAwaiter().GetResult();
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

    private static Task<int> RunApplicationAsync(
        MonitorApplication application,
        CommandLineOptions commandLine,
        CancellationToken cancellationToken) =>
        commandLine.Mode switch
        {
            AppMode.Diagnose => application.DiagnoseAsync(cancellationToken),
            AppMode.HardwareTest => application.HardwareTestAsync(commandLine.NonInteractive, cancellationToken),
            AppMode.LayoutPreview => application.LayoutPreviewAsync(commandLine, cancellationToken),
            AppMode.ListSensors => application.ListSensorsAsync(cancellationToken),
            _ => application.RunAsync(commandLine.Simulation, cancellationToken)
        };
}
