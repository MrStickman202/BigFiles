namespace BigFiles.Core;

public enum GroupKind { App, AppData, Folder, System }

public enum ItemKind { File, Bundle }

/// <summary>What the user sees first: an app (install folder + all its data folders), a user folder, or a system area.</summary>
public sealed class Group
{
    public required string Key { get; init; }
    public required string Name { get; set; }
    public GroupKind Kind { get; set; }
    public InstalledApp? App { get; set; }
    public long Size;
    public long Files;
    public readonly List<Root> Roots = new();
    /// <summary>Files ≥ 1 MB. Everything smaller is only summed into <see cref="Size"/>.</summary>
    public readonly List<Item> Items = new();

    public string KindText => KindLabel(Kind);

    public static string KindLabel(GroupKind k) => k switch
    {
        GroupKind.App => "App",
        GroupKind.AppData => "App data",
        GroupKind.Folder => "Folder",
        _ => "System",
    };

    /// <summary>Roots that still hold something (empty ones are bookkeeping only).</summary>
    public IEnumerable<Root> VisibleRoots => Roots.Where(r => !r.Removed && r.Size > 0);

    public override string ToString() => $"{Name} ({Kind}, {Format.Size(Size)})";
}

/// <summary>A folder (or single file) a group is made of, with the bytes that belong to the group inside it.</summary>
public sealed class Root
{
    public required string Path { get; init; }
    public required Group Group { get; init; }
    /// <summary>True for install folders (Program Files etc.) as opposed to data folders (AppData, ProgramData).</summary>
    public bool IsInstall { get; init; }
    public bool IsFile { get; init; }
    public long Size;
    public long Files;
    public bool Removed;
    public override string ToString() => Path;
}

public sealed class Item
{
    public Item(string path, long size, ItemKind kind, Root root)
    {
        Path = path;
        Size = size;
        Kind = kind;
        Root = root;
    }

    public string Path { get; }
    public string Name => PathUtil.Leaf(Path);
    public long Size { get; }
    public ItemKind Kind { get; }
    public Root Root { get; }
    public Group Group => Root.Group;
    public string KindText => Kind == ItemKind.Bundle ? "Bundle" : "File";
    public override string ToString() => Path;

    /// <summary>Disk images count as one "bundle" item (they're single files, but they're whole drives inside).</summary>
    public static readonly HashSet<string> BundleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vhd", ".vhdx", ".avhd", ".avhdx", ".vmdk", ".vdi", ".qcow2", ".vhdpmem",
    };

    public static ItemKind KindFor(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 && BundleExtensions.Contains(name[dot..]) ? ItemKind.Bundle : ItemKind.File;
    }
}

public sealed class GroupTable
{
    readonly Dictionary<string, Group> byKey = new(StringComparer.Ordinal);
    readonly Dictionary<string, Root> rootsByPath = new(PathUtil.Cmp);

    public readonly List<Group> Groups = new();

    public IEnumerable<Root> AllRoots => rootsByPath.Values;

    public Group Get(string key, string name, GroupKind kind, InstalledApp? app = null)
    {
        if (byKey.TryGetValue(key, out var g))
        {
            if (kind == GroupKind.App && g.Kind == GroupKind.AppData) g.Kind = GroupKind.App;
            g.App ??= app;
            return g;
        }
        g = new Group { Key = key, Name = name, Kind = kind, App = app };
        byKey[key] = g;
        Groups.Add(g);
        return g;
    }

    public Group? Find(string key) => byKey.TryGetValue(key, out var g) ? g : null;

    /// <summary>The root of <paramref name="g"/> at <paramref name="path"/> (created on first use).</summary>
    public Root RootFor(Group g, string path, bool isInstall, bool isFile = false)
    {
        if (rootsByPath.TryGetValue(path, out var r)) return r;
        r = new Root { Path = path, Group = g, IsInstall = isInstall, IsFile = isFile };
        rootsByPath[path] = r;
        g.Roots.Add(r);
        return r;
    }
}
