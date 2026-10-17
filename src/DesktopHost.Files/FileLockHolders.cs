using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DesktopHost.Files;

/// <summary>
/// Names the processes that hold a file open — the question a refused write leaves behind when its exception names
/// only the path.
/// </summary>
/// <remarks>
/// <para>
/// On Windows the Restart Manager lists every process with a handle on a path. A sharing violation or
/// "Access is denied" on a file that something else briefly holds — a virus scanner, a sync client, an indexer, a
/// build running beside the app — is otherwise undiagnosable after the fact. Ask on the failure path only: a
/// Restart Manager session costs milliseconds.
/// </para>
/// <para>
/// Diagnostic and best-effort: it never throws, because a failure to ask must not replace the failure being
/// reported. Off Windows it returns nothing — <c>rename(2)</c> and <c>unlink(2)</c> are not refused by open handles,
/// so a refusal there has a different cause.
/// </para>
/// </remarks>
public static class FileLockHolders
{
    /// <summary>
    /// Describes each process that holds <paramref name="path"/> open as <c>"name (pid)"</c>, the calling process
    /// included.
    /// </summary>
    /// <param name="path">The file to ask about.</param>
    /// <returns>One entry per holder; empty when there is none, when the question failed, or when not on Windows.</returns>
    public static IReadOnlyList<string> Describe(string path)
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            return DescribeWindows(path);
        }
        catch (Exception)
        {
            // A diagnostic of a failure already being reported: asking must never mask it.
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> DescribeWindows(string path)
    {
        var holders = new List<string>();
        if (RmStartSession(out var session, 0, Guid.NewGuid().ToString("N")) != 0) return holders;
        try
        {
            string[] resources = [Path.GetFullPath(path)];
            if (RmRegisterResources(session, (uint)resources.Length, resources, 0, null, 0, null) != 0) return holders;

            uint count = 0, reasons = 0;
            var result = RmGetList(session, out var needed, ref count, null, ref reasons);
            if (result != ErrorMoreData || needed == 0) return holders;

            var infos = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out _, ref count, infos, ref reasons) != 0) return holders;

            for (var i = 0; i < count; i++)
            {
                var pid = infos[i].Process.ProcessId;
                var name = infos[i].AppName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    // The holder can exit between the listing and the lookup; "exited" is then the answer.
                    try { name = Process.GetProcessById(pid).ProcessName; }
                    catch (ArgumentException) { name = "exited"; }
                    catch (InvalidOperationException) { name = "exited"; }
                }
                holders.Add($"{name} ({pid})");
            }
        }
        finally
        {
            _ = RmEndSession(session);
        }
        return holders;
    }

    private const int ErrorMoreData = 234;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, string sessionKey);

    [DllImport("rstrtmgr.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmRegisterResources(
        uint sessionHandle, uint fileCount, string[] fileNames,
        uint applicationCount, [In] RmUniqueProcess[]? applications,
        uint serviceCount, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmGetList(
        uint sessionHandle, out uint procInfoNeeded, ref uint procInfo,
        [In, Out] RmProcessInfo[]? affectedApps, ref uint rebootReasons);
}
