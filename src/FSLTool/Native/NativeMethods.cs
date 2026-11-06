using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FSLTool.Native;

internal static class NativeMethods
{
    // ───────────────────────────── virtdisk.dll ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct VIRTUAL_STORAGE_TYPE
    {
        public uint DeviceId;   // 0 = unknown → detect by extension
        public Guid VendorId;   // Guid.Empty = unknown
    }

    [Flags]
    public enum VirtualDiskAccess : uint
    {
        None = 0,
        AttachRO = 0x00010000,
        AttachRW = 0x00020000,
        Detach = 0x00040000,
        GetInfo = 0x00080000,
        Create = 0x00100000,
        MetaOps = 0x00200000,
        Read = 0x000d0000,
        All = 0x003f0000,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OPEN_VIRTUAL_DISK_PARAMETERS_V1
    {
        public uint Version;  // 1
        public uint RWDepth;  // 1 = only the child (no parents)
    }

    [Flags]
    public enum AttachFlags : uint
    {
        None = 0,
        ReadOnly = 0x1,
        NoDriveLetter = 0x2,
        PermanentLifetime = 0x4,
        NoLocalHost = 0x8,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ATTACH_VIRTUAL_DISK_PARAMETERS
    {
        public uint Version;  // 1
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct COMPACT_VIRTUAL_DISK_PARAMETERS
    {
        public uint Version;  // 1
        public uint Reserved;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    public static extern int OpenVirtualDisk(ref VIRTUAL_STORAGE_TYPE storageType, string path,
        VirtualDiskAccess access, uint flags, ref OPEN_VIRTUAL_DISK_PARAMETERS_V1 parameters,
        out SafeFileHandle handle);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    public static extern int AttachVirtualDisk(SafeFileHandle handle, IntPtr securityDescriptor,
        AttachFlags flags, uint providerSpecificFlags, ref ATTACH_VIRTUAL_DISK_PARAMETERS parameters,
        IntPtr overlapped);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    public static extern int DetachVirtualDisk(SafeFileHandle handle, uint flags, uint providerSpecificFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    public static extern int GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref uint diskPathSizeInBytes,
        [Out] char[] diskPath);

    /// <summary>Backing files of ALL virtual disks attached on this machine (multi-string). Windows 8.1 / Server 2012 R2+.</summary>
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    public static extern int GetAllAttachedVirtualDiskPhysicalPaths(ref uint pathsBufferSizeInBytes, [Out] char[]? pathsBuffer);

    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    public static extern int CompactVirtualDisk(SafeFileHandle handle, uint flags,
        ref COMPACT_VIRTUAL_DISK_PARAMETERS parameters, IntPtr overlapped);

    /// <summary>OPEN_VIRTUAL_DISK_PARAMETERS version 2 (sized for the V3 union member, as the header does).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct OPEN_VIRTUAL_DISK_PARAMETERS_V2
    {
        public uint Version;  // 2
        public int GetInfoOnly;
        public int ReadOnly;
        public Guid ResiliencyGuid;
        public Guid SnapshotId;  // V3 only, keeps the union size right
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenVirtualDisk")]
    public static extern int OpenVirtualDiskV2(ref VIRTUAL_STORAGE_TYPE storageType, string path,
        VirtualDiskAccess access, uint flags, ref OPEN_VIRTUAL_DISK_PARAMETERS_V2 parameters,
        out SafeFileHandle handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct RESIZE_VIRTUAL_DISK_PARAMETERS
    {
        public uint Version;   // 1
        public ulong NewSize;  // 8-byte aligned → offset 8, as in the C union
    }

    /// <summary>VHDX (and on newer Windows VHD): grow the virtual size.</summary>
    [DllImport("virtdisk.dll")]
    public static extern int ResizeVirtualDisk(SafeFileHandle handle, uint flags,
        ref RESIZE_VIRTUAL_DISK_PARAMETERS parameters, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    public struct EXPAND_VIRTUAL_DISK_PARAMETERS
    {
        public uint Version;   // 1
        public ulong NewSize;
    }

    /// <summary>Legacy VHD expansion (handle opened V1 with MetaOps).</summary>
    [DllImport("virtdisk.dll")]
    public static extern int ExpandVirtualDisk(SafeFileHandle handle, uint flags,
        ref EXPAND_VIRTUAL_DISK_PARAMETERS parameters, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    public struct VIRTUAL_DISK_PROGRESS
    {
        public uint OperationStatus;   // ERROR_IO_PENDING while running, then the final Win32 result
        public ulong CurrentValue;
        public ulong CompletionValue;
    }

    [DllImport("virtdisk.dll")]
    public static extern int GetVirtualDiskOperationProgress(SafeFileHandle handle, IntPtr overlapped,
        out VIRTUAL_DISK_PROGRESS progress);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_OPERATION_ABORTED = 995;

    // ───────────────────────────── kernel32: volumes ─────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstVolumeW([Out] char[] volumeName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool FindNextVolumeW(IntPtr hFind, [Out] char[] volumeName, uint bufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FindVolumeClose(IntPtr hFind);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr sa,
        uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
        uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceExW(string directoryName, out ulong freeBytesAvailable,
        out ulong totalBytes, out ulong totalFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FlushFileBuffers(SafeFileHandle handle);

    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint FSCTL_IS_VOLUME_DIRTY = 0x00090078;
    public const uint VOLUME_IS_DIRTY = 0x1;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint DELETE = 0x00010000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    // ───────────────────────────── advapi32: privileges ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint Count;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeFileHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeFileHandle token, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr prev, IntPtr retLen);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Enables a privilege in the current process token (e.g. SeBackupPrivilege).</summary>
    public static bool EnablePrivilege(string name)
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 2;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;
        using (token)
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0;
        }
    }

    // ───────────────────────────── netapi32: open files ─────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FILE_INFO_3
    {
        public uint Id;
        public uint Permissions;
        public uint NumLocks;
        public string PathName;
        public string UserName;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetFileEnum(string? serverName, string? basePath, string? userName, int level,
        out IntPtr bufPtr, int prefMaxLen, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetFileClose(string? serverName, uint fileId);

    [DllImport("netapi32.dll")]
    public static extern int NetApiBufferFree(IntPtr buffer);

    // ───────────────────────────── console (headless mode of the WinExe) ─────────────────────────────

    public const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int processId);

    // ───────────────────────────── wtsapi32: RDS sessions ─────────────────────────────

    public enum WTS_CONNECTSTATE_CLASS
    {
        Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WTS_SESSION_INFO
    {
        public int SessionId;
        public string? WinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    public const int WTSUserName = 5, WTSDomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr WTSOpenServerW(string serverName);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSCloseServer(IntPtr server);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSLogoffSession(IntPtr server, int sessionId, bool wait);

    /// <summary>Title/message lengths are in BYTES.</summary>
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WTSSendMessageW(IntPtr server, int sessionId, string title, int titleLength,
        string message, int messageLength, uint style, int timeoutSeconds, out int response, bool wait);

    public static void Check(int win32Error, string what)
    {
        if (win32Error != 0) throw new Win32Exception(win32Error, $"{what}: {new Win32Exception(win32Error).Message}");
    }
}
