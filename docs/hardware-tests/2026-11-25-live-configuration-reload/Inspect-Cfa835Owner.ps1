param(
    [Parameter(Mandatory)][string]$OutFile,
    [int]$TargetPid = 7428,
    [string]$ComPort = 'COM4'
)

# READ-ONLY. Never opens the COM port and never signals the monitor. Handles are inspected by
# duplicating them into this process; closing a duplicate does not affect the monitor's own handle.

$log = [System.Collections.Generic.List[string]]::new()
function Write-Line([string]$s) { $log.Add($s); Set-Content -LiteralPath $OutFile -Value $log -Encoding utf8 }

try {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    Write-Line ("Elevated: " + $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))

    Write-Line '=== Process'
    $p = Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid"
    if (-not $p) {
        Write-Line "Process $TargetPid not found."
    }
    else {
        Write-Line "Name: $($p.Name)"
        Write-Line "ExecutablePath: $($p.ExecutablePath)"
        Write-Line "CommandLine: $($p.CommandLine)"
        $owner = Invoke-CimMethod -InputObject $p -MethodName GetOwner
        Write-Line "Owner: $($owner.Domain)\$($owner.User)"
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($p.ParentProcessId)"
        Write-Line "Parent: PID $($p.ParentProcessId) $($parent.Name) :: $($parent.CommandLine)"
        if ($p.ExecutablePath) {
            $vi = (Get-Item -LiteralPath $p.ExecutablePath).VersionInfo
            Write-Line "ProductVersion: $($vi.ProductVersion)"
        }
    }

    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class Cfa835HandleScan {
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int retLen);
    [DllImport("ntdll.dll")] static extern int NtQueryObject(IntPtr h, int cls, IntPtr buf, int len, out int retLen);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DuplicateHandle(IntPtr srcProc, IntPtr srcHandle, IntPtr dstProc, out IntPtr dst, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern uint QueryDosDevice(string name, StringBuilder target, int max);

    public static string DosDevice(string name) {
        var sb = new StringBuilder(4096);
        uint n = QueryDosDevice(name, sb, sb.Capacity);
        if (n == 0) return null;
        return sb.ToString();
    }

    static string QueryString(IntPtr h, int cls) {
        int size = 0x1000;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try {
            int ret;
            int st = NtQueryObject(h, cls, buf, size, out ret);
            if ((st == unchecked((int)0xC0000004) || st == unchecked((int)0xC0000023)) && ret > size) {
                Marshal.FreeHGlobal(buf);
                size = ret;
                buf = Marshal.AllocHGlobal(size);
                st = NtQueryObject(h, cls, buf, size, out ret);
            }
            if (st != 0) return null;
            int len = (ushort)Marshal.ReadInt16(buf, 0);
            IntPtr p = Marshal.ReadIntPtr(buf, IntPtr.Size == 8 ? 8 : 4);
            if (len == 0 || p == IntPtr.Zero) return "";
            return Marshal.PtrToStringUni(p, len / 2);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static List<string> Scan(int pid, int timeoutMs) {
        var result = new List<string>();
        int size = 0x400000;
        IntPtr buf;
        int st;
        while (true) {
            buf = Marshal.AllocHGlobal(size);
            int ret;
            st = NtQuerySystemInformation(64 /* SystemExtendedHandleInformation */, buf, size, out ret);
            if (st == unchecked((int)0xC0000004)) {
                Marshal.FreeHGlobal(buf);
                size = Math.Max(size * 2, ret + 0x100000);
                continue;
            }
            break;
        }
        if (st != 0) {
            result.Add("NtQuerySystemInformation failed: 0x" + st.ToString("X8"));
            Marshal.FreeHGlobal(buf);
            return result;
        }

        IntPtr proc = OpenProcess(0x0040 /* PROCESS_DUP_HANDLE */, false, pid);
        if (proc == IntPtr.Zero) {
            result.Add("OpenProcess(PROCESS_DUP_HANDLE) failed: Win32 error " + Marshal.GetLastWin32Error());
            Marshal.FreeHGlobal(buf);
            return result;
        }

        long count = Marshal.ReadIntPtr(buf, 0).ToInt64();
        const int headerSize = 16, entrySize = 40;
        var typeCache = new Dictionary<int, string>();
        int total = 0, files = 0, timedOut = 0;
        for (long i = 0; i < count; i++) {
            IntPtr e = new IntPtr(buf.ToInt64() + headerSize + i * entrySize);
            if (Marshal.ReadIntPtr(e, 8).ToInt64() != pid) continue;
            total++;
            IntPtr hv = Marshal.ReadIntPtr(e, 16);
            uint access = (uint)Marshal.ReadInt32(e, 24);
            int typeIndex = (ushort)Marshal.ReadInt16(e, 30);

            IntPtr dup;
            if (!DuplicateHandle(proc, hv, GetCurrentProcess(), out dup, 0, false, 2 /* DUPLICATE_SAME_ACCESS */)) continue;

            string type;
            if (!typeCache.TryGetValue(typeIndex, out type)) {
                type = QueryString(dup, 2 /* ObjectTypeInformation */) ?? "?";
                typeCache[typeIndex] = type;
            }
            if (type != "File") { CloseHandle(dup); continue; }
            files++;

            // A name query on a synchronous pipe/console handle can block; run it on a throwaway
            // thread and abandon it (and its duplicate) on timeout.
            string name = null;
            IntPtr d = dup;
            var t = new Thread(() => { name = QueryString(d, 1 /* ObjectNameInformation */); });
            t.IsBackground = true;
            t.Start();
            if (!t.Join(timeoutMs)) {
                timedOut++;
                result.Add(string.Format("  0x{0:X4} access=0x{1:X8} <name query timed out: synchronous pipe/console>", hv.ToInt64(), access));
                continue;
            }
            CloseHandle(dup);
            result.Add(string.Format("  0x{0:X4} access=0x{1:X8} {2}", hv.ToInt64(), access, string.IsNullOrEmpty(name) ? "<unnamed>" : name));
        }
        CloseHandle(proc);
        Marshal.FreeHGlobal(buf);
        result.Insert(0, string.Format("Handles in PID {0}: {1} total, {2} File objects, {3} name queries timed out", pid, total, files, timedOut));
        return result;
    }
}
'@

    Write-Line "=== $ComPort device mapping"
    $dev = [Cfa835HandleScan]::DosDevice($ComPort)
    Write-Line "QueryDosDevice($ComPort): $dev"
    $serialComm = Get-ItemProperty 'HKLM:\HARDWARE\DEVICEMAP\SERIALCOMM' -ErrorAction SilentlyContinue
    if ($serialComm) {
        $serialComm.PSObject.Properties | Where-Object { $_.Name -like '\Device\*' } | ForEach-Object { Write-Line "SERIALCOMM: $($_.Name) -> $($_.Value)" }
    }

    Write-Line "=== File handles held by PID $TargetPid"
    $lines = [Cfa835HandleScan]::Scan($TargetPid, 500)
    foreach ($l in $lines) { Write-Line $l }

    Write-Line '=== Verdict'
    if ($dev) {
        $hits = $lines | Where-Object { $_ -match [regex]::Escape($dev) + '(\s|$)' }
        if ($hits) { Write-Line "PID $TargetPid HOLDS $ComPort ($dev):"; $hits | ForEach-Object { Write-Line $_ } }
        else { Write-Line "PID $TargetPid does NOT hold an open handle to $ComPort ($dev)." }
    }
    else { Write-Line "Could not resolve $ComPort to a device object." }
}
catch {
    Write-Line ("ERROR: " + $_.Exception.ToString())
}
finally {
    Write-Line 'DONE'
}
[Environment]::Exit(0)
