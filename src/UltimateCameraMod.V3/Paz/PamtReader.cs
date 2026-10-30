using System.Text;

namespace UltimateCameraMod.Paz;

/// <summary>
/// Parses .pamt index files to discover file entries in PAZ archives.
/// </summary>
/// <remarks>
/// Two layouts are supported:
/// - Legacy: name tables use a u32 parent offset (0xFFFFFFFF = root), names carry their extension,
///   and file records run to the end of the file.
/// - Compact (game update of 2026-10-30): name tables use a varint parent delta (0 = root,
///   otherwise this entry's offset minus the parent's), names have no extension, the record count
///   comes from the folder records, and a trailing extension table is indexed by the top byte of flags.
/// </remarks>
public static class PamtReader
{
    public static List<PazEntry> Parse(string pamtPath, string? pazDir = null)
    {
        byte[] data = File.ReadAllBytes(pamtPath);
        pazDir ??= Path.GetDirectoryName(pamtPath) ?? ".";
        string pamtStem = Path.GetFileNameWithoutExtension(pamtPath);

        int off = 0;
        off += 4; // skip magic

        uint pazCount = BitConverter.ToUInt32(data, off); off += 4;
        off += 8; // hash + zero

        for (int i = 0; i < pazCount; i++)
        {
            off += 4; // hash
            off += 4; // size
            if (i < pazCount - 1)
                off += 4; // separator
        }

        // Legacy folder tables start with a root entry whose parent is 0xFFFFFFFF;
        // compact tables start with a varint delta of 0.
        bool compact = BitConverter.ToUInt32(data, off + 4) != 0xFFFFFFFF;

        // Folder section
        var folders = ReadNameTable(data, ref off, compact);
        string folderPrefix = "";
        foreach (var folder in folders.Values)
        {
            if (folder.Parent < 0)
                folderPrefix = folder.Name;
        }

        // Node section (path tree)
        var nodes = ReadNameTable(data, ref off, compact);

        string BuildPath(uint nodeRef)
        {
            var parts = new List<string>();
            int cur = nodeRef == 0xFFFFFFFF ? -1 : (int)nodeRef;
            int guard = 0;
            while (cur >= 0 && guard < 64)
            {
                if (!nodes.TryGetValue(cur, out var node))
                    break;
                parts.Add(node.Name);
                cur = node.Parent;
                guard++;
            }
            parts.Reverse();
            return string.Concat(parts);
        }

        // Record section
        uint folderCount = BitConverter.ToUInt32(data, off); off += 4;
        off += 4; // hash
        long fileCount = 0;
        for (int i = 0; i < folderCount; i++)
            fileCount += BitConverter.ToUInt32(data, off + i * 16 + 8);
        off += (int)folderCount * 16;

        int recordsEnd = data.Length;
        string[] extensions = Array.Empty<string>();
        if (compact)
        {
            recordsEnd = off + (int)fileCount * 20;
            if (recordsEnd + 4 > data.Length)
                throw new InvalidDataException($"Unexpected archive index layout in {pamtPath}.");
            int extSize = (int)BitConverter.ToUInt32(data, recordsEnd);
            extensions = Encoding.UTF8.GetString(data, recordsEnd + 4, extSize).Split('\0');
        }

        // File records (20 bytes each)
        var entries = new List<PazEntry>();
        while (off + 20 <= recordsEnd)
        {
            uint nodeRef = BitConverter.ToUInt32(data, off);
            uint pazOffset = BitConverter.ToUInt32(data, off + 4);
            uint compSize = BitConverter.ToUInt32(data, off + 8);
            uint origSize = BitConverter.ToUInt32(data, off + 12);
            uint flags = BitConverter.ToUInt32(data, off + 16);
            off += 20;

            int pazIndex = (int)(flags & 0xFF);
            string nodePath = BuildPath(nodeRef);
            if (compact)
            {
                int extIndex = (int)(flags >> 24);
                if (extIndex < extensions.Length)
                    nodePath += extensions[extIndex];
            }
            string fullPath = string.IsNullOrEmpty(folderPrefix)
                ? nodePath
                : $"{folderPrefix}/{nodePath}";

            int pazNum = int.Parse(pamtStem) + pazIndex;
            string pazFile = Path.Combine(pazDir, $"{pazNum}.paz");

            entries.Add(new PazEntry
            {
                Path = fullPath,
                PazFile = pazFile,
                Offset = (int)pazOffset,
                CompSize = (int)compSize,
                OrigSize = (int)origSize,
                Flags = flags,
                PazIndex = pazIndex,
            });
        }

        return entries;
    }

    /// <summary>
    /// Reads a size-prefixed name table, keyed by each entry's offset within the table.
    /// Parent is -1 for root entries.
    /// </summary>
    private static Dictionary<int, (int Parent, string Name)> ReadNameTable(byte[] data, ref int off, bool compact)
    {
        uint size = BitConverter.ToUInt32(data, off); off += 4;
        int start = off;
        int end = start + (int)size;
        var table = new Dictionary<int, (int Parent, string Name)>();
        while (off < end)
        {
            int rel = off - start;
            int parent;
            if (compact)
            {
                uint delta = ReadVarint(data, ref off);
                parent = delta == 0 ? -1 : rel - (int)delta;
            }
            else
            {
                uint raw = BitConverter.ToUInt32(data, off); off += 4;
                parent = raw == 0xFFFFFFFF ? -1 : (int)raw;
            }
            int slen = data[off];
            string name = Encoding.UTF8.GetString(data, off + 1, slen);
            table[rel] = (parent, name);
            off += 1 + slen;
        }
        return table;
    }

    private static uint ReadVarint(byte[] data, ref int off)
    {
        uint value = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[off++];
            value |= (uint)(b & 0x7F) << shift;
            if (b < 0x80)
                return value;
            shift += 7;
        }
    }
}
