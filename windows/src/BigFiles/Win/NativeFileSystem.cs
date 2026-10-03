using System.Runtime.InteropServices;
using BigFiles.Core;
using static BigFiles.Win.Native;

namespace BigFiles.Win;

/// <summary>
/// Lists a directory with GetFileInformationByHandleEx(FileIdBothDirectoryInfo): the same kernel call that
/// FindFirstFileEx(FIND_FIRST_EX_LARGE_FETCH) makes, but it also returns each file's allocation size and
/// file ID, so "size on disk" and hard links need no extra call per file. Files are never opened for reading.
/// Falls back to FindFirstFileEx on file systems that don't support it.
/// </summary>
internal sealed unsafe class NativeFileSystem : IFileSystem
{
    // Offsets in FILE_ID_BOTH_DIR_INFO.
    const int OffNext = 0, OffEndOfFile = 40, OffAllocation = 48, OffAttributes = 56, OffNameLength = 60,
              OffEaSize = 64, OffFileId = 96, OffName = 104;

    readonly byte[] buffer = new byte[64 * 1024];
    readonly Dictionary<string, long> clusterSizes = new(PathUtil.Cmp);

    public bool ReadDirectory(string dir, List<RawEntry> entries)
    {
        using var h = CreateFileW(LongPath(dir), FILE_LIST_DIRECTORY, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                                  FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) return false;

        bool first = true;
        fixed (byte* p = buffer)
        {
            while (true)
            {
                if (!GetFileInformationByHandleEx(h, FileIdBothDirectoryInfo, p, (uint)buffer.Length))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_NO_MORE_FILES || err == ERROR_FILE_NOT_FOUND) return true;
                    if (first && err is ERROR_INVALID_PARAMETER or ERROR_NOT_SUPPORTED or ERROR_INVALID_LEVEL or ERROR_INVALID_FUNCTION)
                    {
                        h.Dispose();
                        return ReadWithFindFirst(dir, entries);
                    }
                    return false;
                }
                first = false;

                byte* e = p;
                while (true)
                {
                    uint next = *(uint*)(e + OffNext);
                    int nameChars = (int)(*(uint*)(e + OffNameLength) / 2);
                    char* name = (char*)(e + OffName);
                    if (!(nameChars == 1 && name[0] == '.') && !(nameChars == 2 && name[0] == '.' && name[1] == '.'))
                    {
                        uint attr = *(uint*)(e + OffAttributes);
                        var entry = new RawEntry
                        {
                            Name = new string(name, 0, nameChars),
                            IsDirectory = (attr & FILE_ATTRIBUTE_DIRECTORY) != 0,
                            FileId = *(long*)(e + OffFileId),
                        };
                        if (entry.FileId == -1) entry.FileId = 0; // FILE_INVALID_FILE_ID (e.g. ReFS): unknown
                        long alloc = *(long*)(e + OffAllocation);
                        uint tag = (attr & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? *(uint*)(e + OffEaSize) : 0;
                        Classify(ref entry, dir, attr, tag, alloc, alloc);
                        entries.Add(entry);
                    }
                    if (next == 0) break;
                    e += next;
                }
            }
        }
    }

    /// <summary>Fills in Skip and Size from the attributes, without opening the file in the common case.</summary>
    static void Classify(ref RawEntry entry, string dir, uint attr, uint reparseTag, long allocation, long fallback)
    {
        if (entry.IsDirectory)
        {
            // Never follow junctions, symlinks, mount points or any other reparse point (AppData has junction
            // loops). The one exception is OneDrive-style cloud folders: they are real local folders that merely
            // carry a cloud tag, and skipping them would hide everything in OneDrive. Cloud folders whose
            // contents aren't on this PC (RECALL_ON_OPEN) are skipped too, so OneDrive never has to fetch them.
            bool cloudFolder = (reparseTag & 0xFFFF0FFF) == IO_REPARSE_TAG_CLOUD;
            entry.Skip = (attr & FILE_ATTRIBUTE_RECALL_ON_OPEN) != 0
                         || ((attr & FILE_ATTRIBUTE_REPARSE_POINT) != 0 && !cloudFolder);
            return;
        }
        if ((attr & FILE_ATTRIBUTE_REPARSE_POINT) != 0 && (reparseTag & IO_REPARSE_TAG_NAME_SURROGATE_BIT) != 0)
        {
            entry.Skip = true; // a symlink to a file: the target is counted where it lives
            return;
        }
        const uint special = FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_COMPRESSED | FILE_ATTRIBUTE_SPARSE_FILE
                             | FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS;
        if ((attr & special) == 0)
        {
            entry.Size = allocation;
            return;
        }
        // Compressed (NTFS / CompactOS), sparse, deduplicated or cloud files: ask for the real space on disk.
        // This reads metadata only, so OneDrive "online-only" files stay online and count as 0.
        bool notLocal = (attr & (FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0;
        uint low = GetCompressedFileSizeW(LongPath(PathUtil.Combine(dir, entry.Name)), out uint high);
        if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)
            entry.Size = notLocal ? 0 : fallback;
        else
            entry.Size = ((long)high << 32) | low;
    }

    bool ReadWithFindFirst(string dir, List<RawEntry> entries)
    {
        long cluster = ClusterSize(PathUtil.Root(dir));
        var h = FindFirstFileExW(LongPath(PathUtil.Combine(dir, "*")), FindExInfoBasic, out var d, FindExSearchNameMatch,
                                 IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);
        if (h == new IntPtr(-1)) return false;
        try
        {
            do
            {
                if (d.cFileName is "." or "..") continue;
                var entry = new RawEntry { Name = d.cFileName, IsDirectory = (d.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 };
                long length = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
                long alloc = cluster > 0 ? (length + cluster - 1) / cluster * cluster : length;
                uint tag = (d.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? d.dwReserved0 : 0;
                Classify(ref entry, dir, d.dwFileAttributes, tag, alloc, alloc);
                entries.Add(entry);
            } while (FindNextFileW(h, out d));
        }
        finally
        {
            FindClose(h);
        }
        return true;
    }

    long ClusterSize(string root)
    {
        if (clusterSizes.TryGetValue(root, out var c)) return c;
        c = GetDiskFreeSpaceW(root, out uint spc, out uint bps, out _, out _) ? (long)spc * bps : 4096;
        clusterSizes[root] = c;
        return c;
    }
}
