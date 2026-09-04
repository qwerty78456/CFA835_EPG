using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Cfa835SystemMonitor;

public interface IShutdownExecutor
{
    void RequestShutdown(int seconds);
    bool TryAbort();
}

public sealed class WindowsShutdownExecutor(ILogger logger) : IShutdownExecutor
{
    // ERROR_NO_SHUTDOWN_IN_PROGRESS: "shutdown /a" when nothing is pending. Benign.
    private const int NoShutdownInProgress = 1116;

    public void RequestShutdown(int seconds) =>
        _ = Run("/s", "/f", "/t", seconds.ToString(CultureInfo.InvariantCulture));

    public bool TryAbort() => Run("/a");

    // Runs on the transport's key-event thread; must never throw.
    private bool Run(params string[] arguments)
    {
        try
        {
            ProcessStartInfo start = new()
            {
                FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(start);
            if (process is null)
            {
                logger.LogWarning("shutdown.exe {Arguments} did not start", string.Join(' ', arguments));
                return false;
            }

            if (!process.WaitForExit(2000))
            {
                logger.LogWarning("shutdown.exe {Arguments} did not exit within 2s", string.Join(' ', arguments));
                return false;
            }

            if (process.ExitCode == NoShutdownInProgress)
            {
                logger.LogInformation("shutdown.exe {Arguments}: no shutdown was in progress", string.Join(' ', arguments));
                return true;
            }

            if (process.ExitCode != 0)
            {
                logger.LogWarning("shutdown.exe {Arguments} exited with code {Code}", string.Join(' ', arguments), process.ExitCode);
                return false;
            }

            logger.LogInformation("shutdown.exe {Arguments} succeeded", string.Join(' ', arguments));
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not run shutdown.exe {Arguments}", string.Join(' ', arguments));
            return false;
        }
    }
}
