using System.Collections.Concurrent;
using System.DirectoryServices;
using System.Security.Principal;
using System.Text;
using FSLTool.Models;

namespace FSLTool.Services;

/// <summary>
/// Account state in Active Directory for containers whose SID still resolves:
/// disabled / expired accounts and the last logon. One LDAP query per domain per 100 SIDs.
/// (Accounts that no longer resolve at all are "orphans" — see ProfileScanner.ResolveAccounts.)
/// </summary>
public static class AdService
{
    private sealed record State(bool Disabled, string? ReasonKey, DateTime? LastLogon);

    /// <summary>SID → state; null = looked up but not found in that domain (local account etc.).</summary>
    private static readonly ConcurrentDictionary<string, State?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private const int UF_ACCOUNTDISABLE = 0x2;
    private const int ChunkSize = 100;

    public static void Resolve(IEnumerable<ProfileInfo> profiles, CancellationToken ct, bool force = false)
    {
        var list = profiles.Where(p => p.Sid != null && !p.IsOrphan && !string.IsNullOrEmpty(p.AccountName)).ToList();
        if (force) foreach (var p in list) _cache.TryRemove(p.Sid!, out _);

        // query only unknown SIDs, grouped by NetBIOS domain from DOMAIN\user
        var todo = list.Where(p => !_cache.ContainsKey(p.Sid!))
            .GroupBy(p => p.AccountName!.Split('\\')[0], StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && !IsLocal(g.Key));

        foreach (var domain in todo)
        {
            ct.ThrowIfCancellationRequested();
            var sids = domain.Select(p => p.Sid!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            try { QueryDomain(domain.Key, sids, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // DC unreachable / no rights: nothing is flagged, retried on the next scan
                Log.Warn(Loc.T("Svc.Ad.CheckFailed", domain.Key, ex.Message));
            }
        }

        foreach (var p in profiles)
        {
            if (p.Sid != null && !p.IsOrphan && _cache.TryGetValue(p.Sid, out var s) && s != null)
                p.SetAdState(true, s.Disabled, s.ReasonKey == null ? null : Loc.T(s.ReasonKey), s.LastLogon);
            else
                p.SetAdState(false, false, null, null);
        }
    }

    private static bool IsLocal(string domain) =>
        domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
        domain.Equals("BUILTIN", StringComparison.OrdinalIgnoreCase) ||
        domain.Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase);

    private static void QueryDomain(string domain, List<string> sids, CancellationToken ct)
    {
        // serverless binding: ADSI finds a DC for the NetBIOS (or DNS) domain name
        using var root = new DirectoryEntry("LDAP://" + domain);
        foreach (var chunk in sids.Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var filter = new StringBuilder("(|");
            foreach (var s in chunk) filter.Append("(objectSid=").Append(EscapeSid(s)).Append(')');
            filter.Append(')');

            using var searcher = new DirectorySearcher(root, filter.ToString(),
                new[] { "objectSid", "userAccountControl", "accountExpires", "lastLogonTimestamp" }, SearchScope.Subtree)
            { PageSize = 500, ClientTimeout = TimeSpan.FromSeconds(30) };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var results = searcher.FindAll())
                foreach (SearchResult r in results)
                {
                    if (First(r, "objectSid") is not byte[] raw) continue;
                    var sid = new SecurityIdentifier(raw, 0).Value;
                    int uac = First(r, "userAccountControl") is int u ? u : 0;
                    var expires = FileTime(First(r, "accountExpires"));
                    var last = FileTime(First(r, "lastLogonTimestamp"));

                    string? reason = null;
                    if ((uac & UF_ACCOUNTDISABLE) != 0) reason = "Svc.Ad.Disabled";
                    else if (expires is DateTime e && e < DateTime.Now) reason = "Svc.Ad.Expired";
                    _cache[sid] = new State(reason != null, reason, last);
                    seen.Add(sid);
                }
            foreach (var s in chunk) if (!seen.Contains(s)) _cache[s] = null;   // not a user of this domain
        }
    }

    private static object? First(SearchResult r, string name) =>
        r.Properties.Contains(name) && r.Properties[name].Count > 0 ? r.Properties[name][0] : null;

    /// <summary>AD large integer (FILETIME); 0 and Int64.MaxValue mean "never".</summary>
    private static DateTime? FileTime(object? v)
    {
        if (v is not long ft || ft <= 0 || ft == long.MaxValue) return null;
        try { return DateTime.FromFileTime(ft); } catch { return null; }
    }

    /// <summary>objectSid in an LDAP filter must be the binary SID escaped as \xx bytes.</summary>
    internal static string EscapeSid(string sid)
    {
        var id = new SecurityIdentifier(sid);
        var bytes = new byte[id.BinaryLength];
        id.GetBinaryForm(bytes, 0);
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes) sb.Append('\\').Append(b.ToString("x2"));
        return sb.ToString();
    }
}
