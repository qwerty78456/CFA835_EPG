using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Cfa835SystemMonitor;

public interface IShutdownExecutor
{
    void RequestShutdown(int seconds);
    void Abort();
}

public sealed class WindowsShutdownExecutor(ILogger<WindowsShutdownExecutor> logger) : IShutdownExecutor
{
    // ERROR_NO_SHUTDOWN_IN_PROGRESS: "shutdown /a" when nothing is pending. Benign.
    private const int NoShutdownInProgress = 1116;

    public void RequestShutdown(int seconds) =>
        Run("/s", "/f", "/t", seconds.ToString(CultureInfo.InvariantCulture));

    public void Abort() => Run("/a");

    // Runs on the transport's key-event thread; must never throw.
    private void Run(params string[] arguments)
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
                return;
            }

            if (!process.WaitForExit(2000))
            {
                logger.LogWarning("shutdown.exe {Arguments} did not exit within 2s", string.Join(' ', arguments));
                return;
            }

            if (process.ExitCode == NoShutdownInProgress)
            {
                logger.LogInformation("shutdown.exe {Arguments}: no shutdown was in progress", string.Join(' ', arguments));
            }
            else if (process.ExitCode != 0)
            {
                logger.LogWarning("shutdown.exe {Arguments} exited with code {Code}", string.Join(' ', arguments), process.ExitCode);
            }
            else
            {
                logger.LogInformation("shutdown.exe {Arguments} succeeded", string.Join(' ', arguments));
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not run shutdown.exe {Arguments}", string.Join(' ', arguments));
        }
    }
}
