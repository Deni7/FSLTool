using System.ComponentModel;
using System.Runtime.InteropServices;
using FSLTool.Native;
using Microsoft.Win32.SafeHandles;
using static FSLTool.Native.NativeMethods;

namespace FSLTool.Services;

/// <summary>
/// A VHD/VHDX attached on this server via virtdisk.dll. The attachment is NOT permanent:
/// if the process dies, Windows detaches the disk automatically when the handle closes.
/// </summary>
public sealed class AttachedDisk : IDisposable
{
    private readonly SafeFileHandle _handle;
    public string ImagePath { get; }
    public int DiskNumber { get; }
    /// <summary>Volume GUID path with trailing backslash, e.g. \\?\Volume{...}\</summary>
    public string VolumePath { get; }
    public bool ReadOnly { get; }

    private AttachedDisk(SafeFileHandle h, string path, int disk, string volume, bool ro)
    { _handle = h; ImagePath = path; DiskNumber = disk; VolumePath = volume; ReadOnly = ro; }

    public static AttachedDisk Attach(string imagePath, bool readOnly, CancellationToken ct = default)
    {
        var handle = VirtualDisk.Open(imagePath, VirtualDiskAccess.All);
        try
        {
            var p = new ATTACH_VIRTUAL_DISK_PARAMETERS { Version = 1 };
            var flags = AttachFlags.NoDriveLetter | (readOnly ? AttachFlags.ReadOnly : AttachFlags.None);
            Check(AttachVirtualDisk(handle, IntPtr.Zero, flags, 0, ref p, IntPtr.Zero), "AttachVirtualDisk");

            int disk = VirtualDisk.GetDiskNumber(handle);
            string? volume = null;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (volume == null && DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                volume = VolumeHelper.FindVolumeOnDisk(disk);
                if (volume == null) Thread.Sleep(500);
            }
            if (volume == null)
            {
                DetachVirtualDisk(handle, 0, 0);
                throw new InvalidOperationException(
                    Loc.T("Svc.Vdisk.NoVolume", disk));
            }
            return new AttachedDisk(handle, imagePath, disk, volume, readOnly);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Detach()
    {
        if (_handle.IsClosed) return;
        DetachVirtualDisk(_handle, 0, 0);
        _handle.Dispose();
    }

    public void Dispose()
    {
        try { Detach(); } catch { /* handle close detaches anyway */ }
    }
}

public static class VirtualDisk
{
    internal static SafeFileHandle Open(string imagePath, VirtualDiskAccess access)
    {
        var type = new VIRTUAL_STORAGE_TYPE();
        var op = new OPEN_VIRTUAL_DISK_PARAMETERS_V1 { Version = 1, RWDepth = 1 };
        Check(OpenVirtualDisk(ref type, imagePath, access, 0, ref op, out var handle), "OpenVirtualDisk");
        return handle;
    }

    internal static int GetDiskNumber(SafeFileHandle handle)
    {
        var buf = new char[260];
        uint size = (uint)(buf.Length * 2);
        Check(GetVirtualDiskPhysicalPath(handle, ref size, buf), "GetVirtualDiskPhysicalPath");
        var s = new string(buf).TrimEnd('\0');             // \\.\PhysicalDrive7
        var digits = new string(s.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.Parse(digits);
    }

    /// <summary>
    /// Backing files of all virtual disks attached on this server — ONE call, no file is opened
    /// (opening hundreds of VHDX files is slow: antivirus scans on open, SMB clients get their leases broken).
    /// Null if the API is not available.
    /// </summary>
    public static HashSet<string>? GetAttachedBackingFiles()
    {
        try
        {
            uint size = 0;
            int rc = GetAllAttachedVirtualDiskPhysicalPaths(ref size, null);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rc == 0 && size == 0) return set;                       // nothing attached
            if (rc != ERROR_INSUFFICIENT_BUFFER && rc != 0) return null;
            for (int attempt = 0; attempt < 3; attempt++)              // a disk may be attached in between
            {
                var buf = new char[size / 2 + 1];
                rc = GetAllAttachedVirtualDiskPhysicalPaths(ref size, buf);
                if (rc == ERROR_INSUFFICIENT_BUFFER) continue;
                if (rc != 0) return null;
                foreach (var path in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    // the paths may come in the \\?\ form — compare with ordinary local paths
                    var p = path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
                          : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
                          : path;
                    set.Add(p);
                }
                return set;
            }
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>Returns true if the image is currently attached on THIS machine.</summary>
    public static bool IsAttachedLocally(string imagePath)
    {
        try
        {
            using var h = Open(imagePath, VirtualDiskAccess.GetInfo | VirtualDiskAccess.Detach);
            var buf = new char[260];
            uint size = (uint)(buf.Length * 2);
            return GetVirtualDiskPhysicalPath(h, ref size, buf) == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Grows the virtual (maximum) size of a DETACHED image. VHDX: ResizeVirtualDisk (handle opened V2, no access mask);
    /// legacy VHD: ExpandVirtualDisk. The partition inside is not touched — see <see cref="StorageWmi.ExtendPartition"/>.
    /// </summary>
    public static void Grow(string imagePath, ulong newSize)
    {
        var type = new VIRTUAL_STORAGE_TYPE();
        if (imagePath.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase))
        {
            using var h = Open(imagePath, VirtualDiskAccess.MetaOps);
            var ep = new EXPAND_VIRTUAL_DISK_PARAMETERS { Version = 1, NewSize = newSize };
            Check(ExpandVirtualDisk(h, 0, ref ep, IntPtr.Zero), "ExpandVirtualDisk");
            return;
        }
        var op = new OPEN_VIRTUAL_DISK_PARAMETERS_V2 { Version = 2 };
        Check(OpenVirtualDiskV2(ref type, imagePath, VirtualDiskAccess.None, 0, ref op, out var handle), "OpenVirtualDisk (v2)");
        using (handle)
        {
            var rp = new RESIZE_VIRTUAL_DISK_PARAMETERS { Version = 1, NewSize = newSize };
            Check(ResizeVirtualDisk(handle, 0, ref rp, IntPtr.Zero), "ResizeVirtualDisk");
        }
    }

    /// <summary>Detaches an image that is attached on this machine (Mount-DiskImage / our own / etc).</summary>
    public static void DetachLocal(string imagePath)
    {
        using var h = Open(imagePath, VirtualDiskAccess.Detach | VirtualDiskAccess.GetInfo);
        Check(DetachVirtualDisk(h, 0, 0), "DetachVirtualDisk");
    }

    /// <summary>
    /// Full compaction: attach read-only (so compaction is file-system aware), compact, detach.
    /// </summary>
    /// <param name="progress">(current, completion) as reported by virtdisk, roughly every 300 ms.</param>
    public static void Compact(string imagePath, Action<ulong, ulong>? progress, CancellationToken ct)
    {
        using var h = Open(imagePath, VirtualDiskAccess.MetaOps | VirtualDiskAccess.AttachRO |
                                      VirtualDiskAccess.Detach | VirtualDiskAccess.GetInfo);
        var ap = new ATTACH_VIRTUAL_DISK_PARAMETERS { Version = 1 };
        int attachErr = AttachVirtualDisk(h, IntPtr.Zero, AttachFlags.ReadOnly | AttachFlags.NoDriveLetter, 0, ref ap, IntPtr.Zero);
        try
        {
            ct.ThrowIfCancellationRequested();
            int err = CompactAsync(h, progress, ct);
            if (err == ERROR_OPERATION_ABORTED) throw new OperationCanceledException(ct);
            if (err != 0 && attachErr != 0)
                throw new Win32Exception(err, $"CompactVirtualDisk (attach RO: {new Win32Exception(attachErr).Message})");
            Check(err, "CompactVirtualDisk");
        }
        finally
        {
            if (attachErr == 0) DetachVirtualDisk(h, 0, 0);
        }
    }

    /// <summary>Runs CompactVirtualDisk with an OVERLAPPED so we can poll progress and cancel it.</summary>
    private static unsafe int CompactAsync(SafeFileHandle h, Action<ulong, ulong>? progress, CancellationToken ct)
    {
        using var done = new ManualResetEvent(false);
        var ov = (System.Threading.NativeOverlapped*)Marshal.AllocHGlobal(sizeof(System.Threading.NativeOverlapped));
        try
        {
            *ov = default;
            ov->EventHandle = done.SafeWaitHandle.DangerousGetHandle();
            var cp = new COMPACT_VIRTUAL_DISK_PARAMETERS { Version = 1 };
            int err = CompactVirtualDisk(h, 0, ref cp, (IntPtr)ov);
            if (err != ERROR_IO_PENDING) return err;   // finished synchronously (or failed to start)

            bool cancelled = false;
            while (true)
            {
                if (!cancelled && ct.IsCancellationRequested)
                {
                    CancelIoEx(h, (IntPtr)ov);
                    cancelled = true;
                }
                done.WaitOne(300);
                if (GetVirtualDiskOperationProgress(h, (IntPtr)ov, out var p) != 0) continue;
                if (p.CompletionValue > 0) progress?.Invoke(p.CurrentValue, p.CompletionValue);
                if (p.OperationStatus != ERROR_IO_PENDING) return (int)p.OperationStatus;
            }
        }
        finally { Marshal.FreeHGlobal((IntPtr)ov); }
    }
}

public static class VolumeHelper
{
    /// <summary>Finds the (largest) volume located on the given physical disk.</summary>
    public static string? FindVolumeOnDisk(int diskNumber)
    {
        var name = new char[MaxPath];
        var find = FindFirstVolumeW(name, MaxPath);
        if (find == new IntPtr(-1)) return null;
        string? best = null; long bestLen = -1;
        try
        {
            do
            {
                var vol = new string(name).TrimEnd('\0');             // \\?\Volume{guid}\
                foreach (var (disk, len) in GetExtents(vol))
                {
                    if (disk == diskNumber && len > bestLen) { best = vol; bestLen = len; }
                }
                Array.Clear(name);
            } while (FindNextVolumeW(find, name, MaxPath));
        }
        finally { FindVolumeClose(find); }

        // the volume must actually be mounted (file system readable)
        if (best != null && !Directory.Exists(best)) return null;
        return best;
    }

    private const int MaxPath = 300;

    private static List<(int disk, long length)> GetExtents(string volume)
    {
        var result = new List<(int, long)>();
        using var h = CreateFileW(volume.TrimEnd('\\'), 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return result;
        const int size = 1024;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, buf, size, out _, IntPtr.Zero))
                return result;
            int count = Marshal.ReadInt32(buf);
            for (int i = 0; i < count; i++)
            {
                // VOLUME_DISK_EXTENTS: DWORD count; [pad 4]; DISK_EXTENT { DWORD disk; [pad 4]; LONGLONG start; LONGLONG len }
                int off = 8 + i * 24;
                if (off + 24 > size) break;
                int disk = Marshal.ReadInt32(buf, off);
                long len = Marshal.ReadInt64(buf, off + 16);
                result.Add((disk, len));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    public static (long total, long free) GetSpace(string volumePath)
    {
        if (!GetDiskFreeSpaceExW(volumePath, out _, out var total, out var free))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDiskFreeSpaceEx");
        return ((long)total, (long)free);
    }

    /// <summary>NTFS "dirty" bit: set after an unclean dismount or detected corruption — chkdsk is due. Null = unknown.</summary>
    public static bool? IsDirty(string volumePath)
    {
        using var h = CreateFileW(volumePath.TrimEnd('\\'), GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var buf = Marshal.AllocHGlobal(4);
        try
        {
            if (!DeviceIoControl(h, FSCTL_IS_VOLUME_DIRTY, IntPtr.Zero, 0, buf, 4, out _, IntPtr.Zero)) return null;
            return ((uint)Marshal.ReadInt32(buf) & VOLUME_IS_DIRTY) != 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Flushes the volume so all deletions (and TRIM notifications) hit the VHDX.</summary>
    public static void Flush(string volumePath)
    {
        using var h = CreateFileW(volumePath.TrimEnd('\\'), GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) FlushFileBuffers(h);
    }
}

/// <summary>Windows Storage Management (root\Microsoft\Windows\Storage) operations on an attached container.</summary>
public static class StorageWmi
{
    private const string Ns = @"\\.\root\Microsoft\Windows\Storage";

    private static string Wql(string s) => s.Replace(@"\", @"\\").Replace("'", @"\'");

    /// <summary>
    /// Extends the partition that holds <paramref name="volumePath"/> to the maximum supported size
    /// (what Resize-Partition -Size (Get-PartitionSupportedSize).SizeMax does). Returns (old, new) partition size.
    /// </summary>
    public static (ulong before, ulong after) ExtendPartition(int diskNumber, string volumePath)
    {
        var scope = new System.Management.ManagementScope(Ns);
        using var s = new System.Management.ManagementObjectSearcher(scope,
            new System.Management.ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber}"));
        var parts = s.Get().Cast<System.Management.ManagementObject>().ToList();
        try
        {
            if (parts.Count == 0) throw new InvalidOperationException(Loc.T("Svc.Vdisk.NoPartitions", diskNumber));
            var target = parts.FirstOrDefault(p => (p["AccessPaths"] as string[])?.Any(a =>
                             a.Equals(volumePath, StringComparison.OrdinalIgnoreCase)) == true)
                         ?? parts.OrderByDescending(p => Convert.ToUInt64(p["Size"])).First();
            ulong before = Convert.ToUInt64(target["Size"]);

            using var sup = target.InvokeMethod("GetSupportedSize", null, null);
            uint rc = Convert.ToUInt32(sup["ReturnValue"]);
            if (rc != 0) throw new InvalidOperationException(Loc.T("Svc.Vdisk.WmiReturned", "MSFT_Partition.GetSupportedSize", rc));
            ulong max = Convert.ToUInt64(sup["SizeMax"]);
            if (max <= before + (1UL << 20)) return (before, before);   // nothing to gain (< 1 MB)

            var inp = target.GetMethodParameters("Resize");
            inp["Size"] = max;
            using var res = target.InvokeMethod("Resize", inp, null);
            rc = Convert.ToUInt32(res["ReturnValue"]);
            if (rc != 0) throw new InvalidOperationException(Loc.T("Svc.Vdisk.WmiReturned", "MSFT_Partition.Resize", rc) + ExtStatus(res));
            return (before, max);
        }
        finally { foreach (var p in parts) p.Dispose(); }
    }

    public enum RepairResult { NoErrors, Fixed, Other }

    /// <summary>Offline scan and fix (chkdsk /f) of a volume that nobody else uses. The volume is dismounted for the duration.</summary>
    public static (RepairResult result, uint output) Repair(string volumePath)
    {
        var scope = new System.Management.ManagementScope(Ns);
        using var s = new System.Management.ManagementObjectSearcher(scope,
            new System.Management.ObjectQuery($"SELECT * FROM MSFT_Volume WHERE Path = '{Wql(volumePath)}'"));
        foreach (System.Management.ManagementObject v in s.Get())
        {
            using (v)
            {
                var inp = v.GetMethodParameters("Repair");
                inp["OfflineScanAndFix"] = true;
                using var res = v.InvokeMethod("Repair", inp, null);
                uint rc = Convert.ToUInt32(res["ReturnValue"]);
                if (rc != 0) throw new InvalidOperationException(Loc.T("Svc.Vdisk.WmiReturned", "MSFT_Volume.Repair", rc) + ExtStatus(res));
                uint output = res["Output"] is null ? 0 : Convert.ToUInt32(res["Output"]);
                // Repair-Volume: 0 = NoErrorsFound, 1 = ErrorsFixed
                return (output switch { 0 => RepairResult.NoErrors, 1 => RepairResult.Fixed, _ => RepairResult.Other }, output);
            }
        }
        throw new InvalidOperationException(Loc.T("Svc.Vdisk.VolumeNotFound"));
    }

    private static string ExtStatus(System.Management.ManagementBaseObject res) =>
        res.Properties.Cast<System.Management.PropertyData>().Any(p => p.Name == "ExtendedStatus") &&
        res["ExtendedStatus"] is string e && e.Length > 0 ? $" ({e})" : "";
}
