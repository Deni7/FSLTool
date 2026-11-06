using System.Buffers.Binary;
using System.Text;

namespace FSLTool.Services;

/// <summary>
/// Reads the maximum (virtual) size of a VHD/VHDX straight from the file headers —
/// no attach required, works on containers opened by session hosts if sharing allows reading.
/// </summary>
public static class VhdHeader
{
    private static readonly Guid MetadataRegion = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    private static readonly Guid VirtualDiskSizeItem = new("2FA54224-CD1B-4876-B211-5DBED83BF4B8");

    public static long? TryReadVirtualSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            return path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) ? ReadVhd(fs) : ReadVhdx(fs);
        }
        catch
        {
            return null;
        }
    }

    private static long? ReadVhdx(FileStream fs)
    {
        var sig = new byte[8];
        fs.ReadExactly(sig);
        if (Encoding.ASCII.GetString(sig) != "vhdxfile") return null;

        foreach (long rtOffset in new long[] { 192 * 1024, 256 * 1024 })
        {
            var rt = new byte[64 * 1024];
            fs.Position = rtOffset;
            fs.ReadExactly(rt);
            if (Encoding.ASCII.GetString(rt, 0, 4) != "regi") continue;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(rt.AsSpan(8));
            for (int i = 0; i < count && 16 + (i + 1) * 32 <= rt.Length; i++)
            {
                var e = rt.AsSpan(16 + i * 32, 32);
                if (new Guid(e[..16]) != MetadataRegion) continue;
                long mdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[16..]);
                return ReadMetadata(fs, mdOffset);
            }
        }
        return null;
    }

    private static long? ReadMetadata(FileStream fs, long mdOffset)
    {
        var table = new byte[64 * 1024];
        fs.Position = mdOffset;
        fs.ReadExactly(table);
        if (Encoding.ASCII.GetString(table, 0, 8) != "metadata") return null;
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(10));
        for (int i = 0; i < count; i++)
        {
            var e = table.AsSpan(32 + i * 32, 32);
            if (new Guid(e[..16]) != VirtualDiskSizeItem) continue;
            uint itemOffset = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
            var v = new byte[8];
            fs.Position = mdOffset + itemOffset;
            fs.ReadExactly(v);
            return (long)BinaryPrimitives.ReadUInt64LittleEndian(v);
        }
        return null;
    }

    private static long? ReadVhd(FileStream fs)
    {
        // Dynamic VHDs keep a copy of the footer at offset 0; fixed VHDs only at the end.
        var buf = new byte[512];
        fs.Position = 0;
        fs.ReadExactly(buf);
        if (Encoding.ASCII.GetString(buf, 0, 8) != "conectix")
        {
            fs.Position = fs.Length - 512;
            fs.ReadExactly(buf);
            if (Encoding.ASCII.GetString(buf, 0, 8) != "conectix") return null;
        }
        return (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(48));
    }
}
