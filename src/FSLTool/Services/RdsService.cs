using System.ComponentModel;
using System.Runtime.InteropServices;
using FSLTool.Native;
using static FSLTool.Native.NativeMethods;

namespace FSLTool.Services;

/// <summary>A user session on an RDS / session host.</summary>
internal sealed record RdsSession(string Host, int Id, string User, WTS_CONNECTSTATE_CLASS State)
{
    public string StateText => State switch
    {
        WTS_CONNECTSTATE_CLASS.Active => Loc.T("Svc.Rds.State.Active"),
        WTS_CONNECTSTATE_CLASS.Disconnected => Loc.T("Svc.Rds.State.Disconnected"),
        WTS_CONNECTSTATE_CLASS.Idle => Loc.T("Svc.Rds.State.Idle"),
        _ => State.ToString(),
    };
    public override string ToString() => Loc.T("Svc.Rds.Session", Host, Id, User, StateText);
}

/// <summary>
/// Remote Desktop Services API (wtsapi32) against the session hosts that hold containers over SMB:
/// find the user's session, send a message to it, log it off. Requires admin rights on the host
/// (or the "Remote Desktop Services" permissions) and RPC access to it.
/// </summary>
internal static class RdsService
{
    /// <summary>
    /// Sessions of any of <paramref name="accounts"/> (DOMAIN\user or user) on the host.
    /// The container's own account is matched first; the SMB client user is a fallback
    /// (it is HOST$ when FSLogix accesses the share as the computer object).
    /// </summary>
    public static List<RdsSession> FindSessions(string host, IEnumerable<string?> accounts)
    {
        var wanted = accounts.Where(a => !string.IsNullOrWhiteSpace(a) && !a!.EndsWith('$'))
                             .Select(a => a!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<RdsSession>();
        if (wanted.Count == 0) return result;

        var server = Open(host);
        try
        {
            if (!WTSEnumerateSessionsW(server, 0, 1, out var buf, out int count))
                throw new Win32Exception(Marshal.GetLastWin32Error(), Loc.T("Svc.Rds.EnumFailed", host));
            try
            {
                int size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (int i = 0; i < count; i++)
                {
                    var si = Marshal.PtrToStructure<WTS_SESSION_INFO>(buf + i * size);
                    if (si.SessionId == 0) continue;   // services session
                    var user = Query(server, si.SessionId, WTSUserName);
                    if (string.IsNullOrEmpty(user)) continue;
                    var domain = Query(server, si.SessionId, WTSDomainName);
                    var full = string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                    if (wanted.Any(w => Matches(w, domain, user)))
                        result.Add(new RdsSession(host, si.SessionId, full, si.State));
                }
            }
            finally { WTSFreeMemory(buf); }
        }
        finally { WTSCloseServer(server); }
        return result;
    }

    /// <summary>"DOMAIN\user" must match both parts; a bare "user" matches the user name only.</summary>
    private static bool Matches(string wanted, string? domain, string user)
    {
        var i = wanted.IndexOf('\\');
        if (i < 0) return wanted.Equals(user, StringComparison.OrdinalIgnoreCase);
        return wanted[(i + 1)..].Equals(user, StringComparison.OrdinalIgnoreCase) &&
               (string.IsNullOrEmpty(domain) || wanted[..i].Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                // FQDN vs NetBIOS domain (contoso.local\user vs CONTOSO)
                wanted[..i].Split('.')[0].Equals(domain.Split('.')[0], StringComparison.OrdinalIgnoreCase));
    }

    public static void SendMessage(RdsSession s, string title, string message, int timeoutSeconds = 0)
    {
        var server = Open(s.Host);
        try
        {
            const uint MB_OK_ICONINFORMATION = 0x40;
            if (!WTSSendMessageW(server, s.Id, title, title.Length * 2, message, message.Length * 2,
                                 MB_OK_ICONINFORMATION, timeoutSeconds, out _, false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"WTSSendMessage ({s})");
        }
        finally { WTSCloseServer(server); }
    }

    /// <summary>Logs the session off and waits until it is gone (FSLogix then detaches the container itself).</summary>
    public static void Logoff(RdsSession s)
    {
        var server = Open(s.Host);
        try
        {
            if (!WTSLogoffSession(server, s.Id, true))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"WTSLogoffSession ({s})");
        }
        finally { WTSCloseServer(server); }
    }

    private static IntPtr Open(string host)
    {
        var h = WTSOpenServerW(host);
        if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"WTSOpenServer({host})");
        return h;
    }

    private static string? Query(IntPtr server, int id, int cls)
    {
        if (!WTSQuerySessionInformationW(server, id, cls, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }
}
