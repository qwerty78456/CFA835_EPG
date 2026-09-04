using System.Security.Principal;

namespace Cfa835SystemMonitor;

public static class WindowsSecurity
{
    /// <summary>Whether the current process has an enabled local Administrators SID.</summary>
    public static bool IsElevatedAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
