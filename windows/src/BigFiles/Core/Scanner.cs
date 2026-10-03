using System.Diagnostics;

namespace BigFiles.Core;

/// <summary>One directory entry as the file system reports it.</summary>
public struct RawEntry
{
    public string Name;
    public bool IsDirectory;
    /// <summary>Don't count or descend (symlinks, junctions, cloud folders that aren't on disk).</summary>
    public bool Skip;
    /// <summary>Space actually used on disk.</summary>
    public long Size;
    /// <summary>File ID for counting hard links once; 0 = unknown.</summary>
    public long FileId;
}

/// <summary>Lists directories. The real one is Win\NativeFileSystem; tests use an in-memory tree.</summary>
public interface IFileSystem
{
    /// <summary>Appends the entries of <paramref name="dir"/>; false if it couldn't be read.</summary>
    bool ReadDirectory(string dir, List<RawEntry> entries);
}

/// <summary>Everything a scan found. Group totals are kept up to date when things are deleted.</summary>
public sealed class ScanData
{
    public required Classifier Classifier { get; init; }
    public GroupTable Table => Classifier.Table;
    public KnownPaths Known => Classifier.Known;
    public required IReadOnlyList<string> ScanRoots { get; init; }
    public long Files;
    public long Bytes;
    public int Errors;
    public TimeSpan Elapsed;
    public bool Cancelled;
}

/// <summary>
/// Single-pass, depth-first walk on one background thread. Never follows reparse points,
/// never opens files, keeps only items ≥ 1 MB.
/// </summary>
public sealed class Scanner
{
    public const long ItemThreshold = 1024 * 1024;

    readonly Classifier classifier;
    readonly IFileSystem fs;
    volatile bool cancel;

    // Read by the UI thread for progress (torn reads are harmless here).
    public long Files;
    public long Bytes;
    public volatile string CurrentPath = "";

    public Scanner(Classifier classifier, IFileSystem fs)
    {
        this.classifier = classifier;
        this.fs = fs;
    }

    public void Cancel() => cancel = true;
    public bool IsCancelled => cancel;

    public ScanData Run(IEnumerable<string> roots)
    {
        var sw = Stopwatch.StartNew();
        var rootList = PathUtil.RemoveNested(roots);
        var data = new ScanData { Classifier = classifier, ScanRoots = rootList };
        var linksByVolume = new Dictionary<string, LongSet>(PathUtil.Cmp);
        var stack = new Stack<Frame>();
        for (int i = rootList.Count - 1; i >= 0; i--)
        {
            var f = classifier.FrameFor(rootList[i]);
            var vol = PathUtil.Root(rootList[i]);
            if (!linksByVolume.TryGetValue(vol, out var set)) linksByVolume[vol] = set = new LongSet();
            f.Links = set;
            stack.Push(f);
        }

        var entries = new List<RawEntry>(512);
        var subdirs = new List<Frame>(64);
        long files = 0, bytes = 0;
        int errors = 0;

        while (stack.Count > 0)
        {
            if (cancel) { data.Cancelled = true; break; }
            var f = stack.Pop();
            CurrentPath = f.Path;
            entries.Clear();
            if (!fs.ReadDirectory(f.Path, entries))
            {
                errors++;
                if (entries.Count == 0) continue;
            }

            subdirs.Clear();
            foreach (var e in entries)
            {
                if (e.Skip) continue;
                if (e.IsDirectory)
                {
                    subdirs.Add(classifier.Enter(f, PathUtil.Combine(f.Path, e.Name), e.Name));
                    continue;
                }

                long size = e.Size;
                // Count hard-linked files once (the same file ID seen again on this volume).
                if (e.FileId != 0 && size > 0 && f.Links != null && !f.Links.Add(e.FileId)) continue;

                // Only build the full path when it's needed (most files are small).
                string? path = f.Anchor == Anchor.DriveRoot || size >= ItemThreshold ? PathUtil.Combine(f.Path, e.Name) : null;
                var root = path != null ? classifier.FileRoot(f, path, e.Name) : f.Root;
                root.Size += size;
                root.Files++;
                var g = root.Group;
                g.Size += size;
                g.Files++;
                files++;
                bytes += size;
                if (size >= ItemThreshold) g.Items.Add(new Item(path!, size, Item.KindFor(e.Name), root));
            }
            Files = files;
            Bytes = bytes;
            for (int i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
        }

        data.Files = files;
        data.Bytes = bytes;
        data.Errors = errors;
        data.Elapsed = sw.Elapsed;
        return data;
    }
}
