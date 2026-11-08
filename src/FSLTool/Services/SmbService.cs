using System.Collections.Concurrent;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using FSLTool.Models;
using FSLTool.Native;

namespace FSLTool.Services;

/// <summary>
/// Open SMB files on this server (who has which container mounted) and forced close.
/// Primary source: WMI MSFT_SmbOpenFile (gives client computer name); fallback: NetFileEnum.
/// </summary>
public static class SmbService
{
    public static List<SmbHandle> GetOpenFiles()
    {
        try { return GetViaWmi(); }
        catch (Exception ex)
        {
            Log.Warn(Loc.T("Svc.Smb.WmiUnavailable", ex.Message));
            LastTimings = Loc.T("Svc.Smb.TimingsNetFileEnum");
            return GetViaNetApi();
        }
    }

    private const string Fields = "SELECT FileId, Path, ClientComputerName, ClientUserName FROM MSFT_SmbOpenFile";

    /// <summary>Breakdown of the last full query (for the scan timings in the log).</summary>
    public static string LastTimings { get; private set; } = "";

    private static List<SmbHandle> GetViaWmi()
    {
        // A file server holds thousands of open files (Office docs, etc.) — only fetch VHD(X) ones.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<(string id, string path, string client, string user)> raw;
        try { raw = QueryWmi(Fields + " WHERE Path LIKE '%.vhd%'"); }
        catch (ManagementException) { raw = QueryWmi(Fields); }
        long wmiMs = sw.ElapsedMilliseconds;

        // Reverse DNS of client IPs: unique IPs only, in parallel, with a timeout
        // (sequential lookups of hosts without PTR records used to cost seconds each).
        var names = ResolveClients(raw.Select(r => r.client).Distinct(StringComparer.OrdinalIgnoreCase));
        LastTimings = Loc.T("Svc.Smb.Timings", wmiMs, sw.ElapsedMilliseconds - wmiMs);

        return raw.Where(r => IsVhd(r.path)).Select(r => new SmbHandle
        {
            FileId = r.id,
            LocalPath = r.path,
            ClientComputer = names.TryGetValue(r.client, out var n) ? n : r.client,
            ClientUser = r.user,
        }).ToList();
    }

    private static List<(string, string, string, string)> QueryWmi(string wql)
    {
        var list = new List<(string, string, string, string)>();
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\SMB");
        var opts = new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), opts);
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                if (mo["Path"] is not string path || !IsVhd(path)) continue;
                list.Add((Convert.ToString(mo["FileId"]) ?? "", path,
                          mo["ClientComputerName"] as string ?? "", mo["ClientUserName"] as string ?? ""));
            }
        }
        return list;
    }

    /// <summary>
    /// Fast check for ONE file: SMB opens on this server whose local path starts with <paramref name="localPath"/>
    /// (NetFileEnum filters by path on the server side; no WMI, no DNS). No client host names.
    /// Returns null if the API is not available (then use <see cref="GetOpenFiles"/>).
    /// </summary>
    public static List<SmbHandle>? FindOpens(string localPath)
    {
        int rc = NativeMethods.NetFileEnum(null, localPath, null, 3, out var buf, -1, out int read, out _, IntPtr.Zero);
        if (rc != 0) return null;
        var list = new List<SmbHandle>();
        try
        {
            int sz = Marshal.SizeOf<NativeMethods.FILE_INFO_3>();
            for (int i = 0; i < read; i++)
            {
                var fi = Marshal.PtrToStructure<NativeMethods.FILE_INFO_3>(buf + i * sz);
                if (fi.PathName == null || !fi.PathName.Equals(localPath, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new SmbHandle { FileId = fi.Id.ToString(), LocalPath = fi.PathName, ClientUser = fi.UserName ?? "", FromNetApi = true });
            }
        }
        finally { if (buf != IntPtr.Zero) NativeMethods.NetApiBufferFree(buf); }
        return list;
    }

    private static List<SmbHandle> GetViaNetApi()
    {
        var list = new List<SmbHandle>();
        int rc = NativeMethods.NetFileEnum(null, null, null, 3, out var buf, -1, out int read, out _, IntPtr.Zero);
        if (rc != 0) { Log.Warn(Loc.T("Svc.Smb.NetFileEnumError", rc)); return list; }
        try
        {
            int sz = Marshal.SizeOf<NativeMethods.FILE_INFO_3>();
            for (int i = 0; i < read; i++)
            {
                var fi = Marshal.PtrToStructure<NativeMethods.FILE_INFO_3>(buf + i * sz);
                if (fi.PathName == null || !IsVhd(fi.PathName)) continue;
                list.Add(new SmbHandle
                {
                    FileId = fi.Id.ToString(),
                    LocalPath = fi.PathName,
                    ClientUser = fi.UserName ?? "",
                    ClientComputer = "",
                    FromNetApi = true,
                });
            }
        }
        finally { NativeMethods.NetApiBufferFree(buf); }
        return list;
    }

    public static void ForceClose(SmbHandle h)
    {
        if (!h.FromNetApi)
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\SMB");
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT * FROM MSFT_SmbOpenFile WHERE FileId = {h.FileId}"));
                foreach (ManagementObject mo in searcher.Get())
                    using (mo) mo.InvokeMethod("ForceClose", null);
                return;
            }
            catch (Exception ex)
            {
                Log.Warn(Loc.T("Svc.Smb.ForceCloseFallback", ex.Message));
            }
        }
        if (!ulong.TryParse(h.FileId, out var id))
            throw new InvalidOperationException(Loc.T("Svc.Smb.BadFileId", h.FileId));
        int rc = NativeMethods.NetFileClose(null, (uint)(id & 0xFFFFFFFF));
        if (rc != 0 && rc != 2314 /* NERR_FileIdNotFound */)
            throw new InvalidOperationException(Loc.T("Svc.Smb.NetFileCloseError", rc));
    }

    private static bool IsVhd(string p) =>
        p.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> _dns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MSFT_SmbOpenFile returns client IPs; turn them into host names (cached, parallel, 3 s budget).</summary>
    private static Dictionary<string, string> ResolveClients(IEnumerable<string> clients)
    {
        var todo = new List<(string client, IPAddress ip)>();
        lock (_dns)
            foreach (var c in clients)
                if (!_dns.ContainsKey(c) && IPAddress.TryParse(c.Trim('[', ']'), out var ip)) todo.Add((c, ip));

        if (todo.Count > 0)
        {
            var tasks = todo.Select(async t =>
            {
                try
                {
                    // ConfigureAwait(false): the caller blocks in Task.WaitAll — resuming on its (UI) context would deadlock
                    var e = await Dns.GetHostEntryAsync(t.ip).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    lock (_dns) _dns[t.client] = e.HostName.Split('.')[0].ToUpperInvariant();
                }
                catch { lock (_dns) _dns.TryAdd(t.client, t.client); } // no PTR / timeout: keep the IP
            }).ToArray();
            Task.WaitAll(tasks);
        }
        lock (_dns) return new Dictionary<string, string>(_dns, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Maps \\THISSERVER\share\... to local paths so they can be matched with SMB open files.</summary>
public static class PathMapper
{
    // called for every container during a scan (in parallel) — everything here is cached
    private static readonly Lazy<Dictionary<string, string>> _shares = new(LoadShares);
    private static readonly ConcurrentDictionary<string, bool> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public static string ToLocal(string path)
    {
        if (!path.StartsWith(@"\\") || path.StartsWith(@"\\?\")) return path;
        var parts = path.TrimStart('\\').Split('\\', 3);
        if (parts.Length < 2 || !_hosts.GetOrAdd(parts[0], IsThisHost)) return path;
        if (!_shares.Value.TryGetValue(parts[1], out var local)) return path;
        return parts.Length == 3 ? Path.Combine(local, parts[2]) : local;
    }

    public static bool IsLocal(string path) => !ToLocal(path).StartsWith(@"\\");

    private static bool IsThisHost(string host)
    {
        var h = host.Split('.')[0];
        if (h.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1")
            return true;
        try
        {
            if (IPAddress.TryParse(host, out var ip))
                return Dns.GetHostAddresses(Dns.GetHostName()).Any(a => a.Equals(ip));
        }
        catch { }
        return false;
    }

    private static Dictionary<string, string> LoadShares()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, Path FROM Win32_Share WHERE Type = 0 OR Type = 2147483648");
            foreach (ManagementObject mo in s.Get())
                using (mo)
                    if (mo["Name"] is string n && mo["Path"] is string p && p.Length > 0) d[n] = p;
        }
        catch (Exception ex) { Log.Warn("Win32_Share: " + ex.Message); }
        return d;
    }
}
