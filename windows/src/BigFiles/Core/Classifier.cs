namespace BigFiles.Core;

/// <summary>What a folder means for the folders inside it.</summary>
public enum Anchor : byte
{
    None,
    /// <summary>A drive root: top-level folders are named after the app (or are user folders).</summary>
    DriveRoot,
    /// <summary>Program Files, AppData\Local\Programs: each folder inside is an app's install folder.</summary>
    InstallFolder,
    /// <summary>ProgramData, AppData\Local|Roaming|LocalLow: each folder inside is an app's data folder.</summary>
    AppFolder,
    /// <summary>A folder that didn't match an app ("Google", "Riot Games"): look one level deeper.</summary>
    Vendor,
    /// <summary>A folder that belongs to an app: only a different app's registered install folder splits off.</summary>
    AppDir,
    /// <summary>AppData\Local\Packages: folders are package family names.</summary>
    Packages,
    /// <summary>Program Files\WindowsApps: folders are package full names.</summary>
    WindowsApps,
    /// <summary>"Epic Games", "XboxGames", …: each folder inside is its own game.</summary>
    GameLibrary,
    SteamApps,
    /// <summary>steamapps\common: each folder inside is its own game.</summary>
    SteamCommon,
    UsersRoot,
    Profile,
}

/// <summary>Classification state of one directory while scanning.</summary>
public struct Frame
{
    public string Path;
    /// <summary>The group root that files directly in this folder are added to.</summary>
    public Root Root;
    public Anchor Anchor;
    /// <summary>Inside a fixed area (C:\Windows, Recycle Bin…): no more classification below.</summary>
    public bool Fixed;
    /// <summary>Inside an install area (Program Files, game libraries…) rather than a data area.</summary>
    public bool Install;
    /// <summary>For <see cref="Anchor.Vendor"/>: the vendor folder's name, to try "Google" + "Chrome".</summary>
    public string? Vendor;
    /// <summary>Hard-link bookkeeping for the volume this folder is on (used by the scanner).</summary>
    public LongSet? Links;

    public Group Group => Root.Group;
}

/// <summary>
/// Decides which group every folder belongs to. Called once per directory, so the common path is
/// a couple of hash lookups.
/// </summary>
public sealed class Classifier
{
    public readonly GroupTable Table = new();
    public KnownPaths Known { get; }
    public AppIndex Apps { get; }

    readonly Dictionary<string, Func<Frame, string, Frame>> known = new(PathUtil.Cmp);
    readonly Group other, windows, winFiles, recycle;

    static readonly HashSet<string> GameLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "Epic Games", "XboxGames", "GOG Games", "EA Games", "Origin Games",
    };

    /// <summary>Top-level folders that Windows itself manages.</summary>
    static readonly HashSet<string> SystemTopFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "Recovery", "PerfLogs", "Config.Msi", "Boot", "EFI", "MSOCache",
        "OneDriveTemp", "Documents and Settings",
    };

    /// <summary>Files at a drive root that belong to Windows.</summary>
    public static readonly HashSet<string> SystemRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp",
    };

    public Classifier(KnownPaths k, AppIndex apps)
    {
        Known = k;
        Apps = apps;
        other = Table.Get("sys:other", "Other", GroupKind.System);
        windows = Table.Get("sys:windows", "Windows", GroupKind.System);
        winFiles = Table.Get("sys:winfiles", "Windows system files", GroupKind.System);
        recycle = Table.Get("sys:recycle", "Recycle Bin", GroupKind.System);

        known[k.WindowsDir] = (p, path) => FixedFrame(path, Table.RootFor(windows, path, false));
        known[k.ProgramFiles] = (p, path) => Inherit(p, path, Anchor.InstallFolder, install: true);
        known[k.ProgramFilesX86] = (p, path) => Inherit(p, path, Anchor.InstallFolder, install: true);
        known[k.ProgramData] = (p, path) => Inherit(p, path, Anchor.AppFolder, install: false);
        known[k.UsersRoot] = (p, path) => Inherit(p, path, Anchor.UsersRoot, install: false);
        known[k.Profile] = (p, path) => NewGroupFrame(p, path, Table.Get("folder:~", "~", GroupKind.Folder), Anchor.Profile, false);
        known[k.AppData] = (p, path) => NewGroupFrame(p, path, Table.Get("folder:~\\appdata", "~\\AppData", GroupKind.Folder), Anchor.None, false);
        known[k.LocalAppData] = (p, path) => Inherit(p, path, Anchor.AppFolder, install: false);
        known[k.RoamingAppData] = (p, path) => Inherit(p, path, Anchor.AppFolder, install: false);
        known[k.LocalLow] = (p, path) => Inherit(p, path, Anchor.AppFolder, install: false);
        known[PathUtil.Combine(k.LocalAppData, "Programs")] = (p, path) => Inherit(p, path, Anchor.InstallFolder, install: true);
        known[PathUtil.Combine(k.LocalAppData, "Packages")] = (p, path) => Inherit(p, path, Anchor.Packages, install: false);
        known[PathUtil.Combine(k.LocalAppData, "Temp")] = (p, path) =>
            FixedFrame(path, Table.RootFor(Table.Get("sys:temp", "Temporary files", GroupKind.System), path, false));
        known[PathUtil.Combine(k.ProgramFiles, "WindowsApps")] = (p, path) => Inherit(p, path, Anchor.WindowsApps, install: true);
        foreach (var pf in new[] { k.ProgramFiles, k.ProgramFilesX86 })
            known[PathUtil.Combine(pf, "Common Files")] = (p, path) =>
                NewGroupFrame(p, path, Table.Get("sys:commonfiles", "Common Files", GroupKind.System), Anchor.Vendor, true);
    }

    public Group OtherGroup => other;

    /// <summary>The frame of a drive root ("C:\"): loose files there are "Other".</summary>
    public Frame DriveFrame(string root) => new()
    {
        Path = root,
        Root = Table.RootFor(other, root, false),
        Anchor = Anchor.DriveRoot,
    };

    /// <summary>Classifies a scan root by walking down from its drive root (no disk access).</summary>
    public Frame FrameFor(string path)
    {
        var root = PathUtil.Root(path);
        var f = DriveFrame(root);
        var cur = root;
        foreach (var c in PathUtil.Components(path))
        {
            cur = PathUtil.Combine(cur, c);
            f = Enter(f, cur, c);
        }
        return f;
    }

    /// <summary>The root a file directly inside <paramref name="parent"/> counts towards.</summary>
    public Root FileRoot(in Frame parent, string path, string name)
    {
        if (parent.Anchor == Anchor.DriveRoot && SystemRootFiles.Contains(name))
            return Table.RootFor(winFiles, path, false, isFile: true);
        return parent.Root;
    }

    public Frame Enter(in Frame parent, string path, string name)
    {
        if (parent.Fixed) return new Frame { Path = path, Root = parent.Root, Fixed = true, Install = parent.Install, Links = parent.Links };
        Frame f = EnterCore(parent, path, name);
        f.Links = parent.Links;
        if (f.Anchor == Anchor.GameLibrary) f.Vendor ??= name;
        return f;
    }

    Frame EnterCore(in Frame parent, string path, string name)
    {
        if (known.TryGetValue(path, out var make)) return make(parent, path);

        var nameAnchor = Anchor.None;
        if (name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) nameAnchor = Anchor.SteamApps;
        else if (parent.Anchor == Anchor.SteamApps && name.Equals("common", StringComparison.OrdinalIgnoreCase)) nameAnchor = Anchor.SteamCommon;
        else if (GameLibraries.Contains(name)) nameAnchor = Anchor.GameLibrary;

        // A folder that is exactly an app's registered install folder always belongs to that app.
        var app = Apps.ByLocation(path);
        if (app != null) return AppFrame(parent, path, app, true, nameAnchor != Anchor.None ? nameAnchor : Anchor.AppDir);

        switch (parent.Anchor)
        {
            case Anchor.DriveRoot:
                return DriveChild(parent, path, name, nameAnchor);

            case Anchor.InstallFolder:
                return AppFolderChild(parent, path, name, GroupKind.AppData, true, nameAnchor);

            case Anchor.AppFolder:
                return AppFolderChild(parent, path, name, GroupKind.AppData, false, nameAnchor);

            case Anchor.Vendor when nameAnchor != Anchor.None:
            case Anchor.AppDir when nameAnchor != Anchor.None:
                // "steamapps", "Epic Games"…: structural folders never become one app themselves.
                return Inherit(parent, path, nameAnchor, parent.Install);

            case Anchor.Vendor:
            {
                // "Google\Chrome": the registered install folder is inside, or the name matches.
                var a = Apps.UnderLocation(path, unique: true)
                        ?? Apps.ByName(name, allowSuffix: false)
                        ?? (parent.Vendor != null ? Apps.ByName(parent.Vendor + " " + name, allowSuffix: true) : null);
                if (a != null) return AppFrame(parent, path, a, parent.Install, nameAnchor != Anchor.None ? nameAnchor : Anchor.AppDir);
                return Inherit(parent, path, nameAnchor, parent.Install);
            }

            case Anchor.AppDir:
            {
                // Another app installed inside this app's folder, in a subfolder named after it
                // (e.g. "Microsoft\Edge" when "Microsoft" itself is registered to some app).
                var a = Apps.UnderLocation(path, except: parent.Group.App);
                if (a != null && Apps.ByName(name, allowSuffix: true) == a) return AppFrame(parent, path, a, true, Anchor.AppDir);
                return Inherit(parent, path, nameAnchor, parent.Install);
            }

            case Anchor.Packages:
            {
                var a = Apps.ByPackageFamily(name);
                var g = a != null ? AppGroup(a) : Table.Get("pkg:" + name.ToLowerInvariant(), AppIndex.PrettyPackageName(name), GroupKind.AppData);
                return NewGroupFrame(parent, path, g, Anchor.None, false);
            }

            case Anchor.WindowsApps:
            {
                var pfn = AppIndex.FamilyFromFullName(name);
                if (pfn == null) return Inherit(parent, path, Anchor.None, true);
                var a = Apps.ByPackageFamily(pfn);
                var g = a != null ? AppGroup(a) : Table.Get("pkg:" + pfn.ToLowerInvariant(), AppIndex.PrettyPackageName(pfn), GroupKind.App);
                return NewGroupFrame(parent, path, g, Anchor.None, true);
            }

            case Anchor.GameLibrary:
            case Anchor.SteamCommon:
            {
                var a = Apps.UnderLocation(path, unique: true)
                        ?? Apps.ByName(name, allowSuffix: false)
                        ?? (parent.Vendor != null ? Apps.ByName(parent.Vendor + " " + name, allowSuffix: false) : null);
                if (a != null) return AppFrame(parent, path, a, true, Anchor.AppDir);
                return NewGroupFrame(parent, path, NameGroup(name, GroupKind.App), Anchor.AppDir, true);
            }

            case Anchor.UsersRoot:
                return NewGroupFrame(parent, path, Table.Get("folder:" + path.ToLowerInvariant(), path, GroupKind.Folder), Anchor.None, false);

            case Anchor.Profile:
            {
                var pretty = "~\\" + name;
                return NewGroupFrame(parent, path, Table.Get("folder:" + pretty.ToLowerInvariant(), pretty, GroupKind.Folder), Anchor.None, false);
            }

            default:
                return Inherit(parent, path, nameAnchor, parent.Install);
        }
    }

    Frame DriveChild(in Frame parent, string path, string name, Anchor nameAnchor)
    {
        if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
            return FixedFrame(path, Table.RootFor(recycle, path, false));
        if (name.Equals("Windows.old", StringComparison.OrdinalIgnoreCase))
            return FixedFrame(path, Table.RootFor(Table.Get("sys:windows.old", "Windows.old", GroupKind.System), path, false));
        if (name.StartsWith('$') || SystemTopFolders.Contains(name))
            return FixedFrame(path, Table.RootFor(winFiles, path, false));
        if (name.Equals("Program Files", StringComparison.OrdinalIgnoreCase) || name.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase))
            return Inherit(parent, path, Anchor.InstallFolder, install: true);
        if (name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase))
            return Inherit(parent, path, Anchor.AppFolder, install: false);
        if (name.Equals("Users", StringComparison.OrdinalIgnoreCase))
            return Inherit(parent, path, Anchor.UsersRoot, install: false);
        // Any other top-level folder ("C:\Riot Games", "D:\SteamLibrary", "D:\Photos").
        return AppFolderChild(parent, path, name, GroupKind.Folder, true, nameAnchor);
    }

    /// <summary>A folder directly inside Program Files / AppData / a drive root: the app it's named after, or its own group.</summary>
    Frame AppFolderChild(in Frame parent, string path, string name, GroupKind fallbackKind, bool install, Anchor nameAnchor)
    {
        var app = Apps.ByName(name, allowSuffix: true);
        if (app != null) return AppFrame(parent, path, app, install, nameAnchor != Anchor.None ? nameAnchor : Anchor.AppDir);

        var g = fallbackKind == GroupKind.Folder
            ? Table.Get("folder:" + path.ToLowerInvariant(), path, GroupKind.Folder)
            : NameGroup(name, fallbackKind);
        var f = NewGroupFrame(parent, path, g, nameAnchor != Anchor.None ? nameAnchor : Anchor.Vendor, install);
        f.Vendor = name;
        return f;
    }

    Group NameGroup(string name, GroupKind kind)
    {
        var key = NameKey.Alnum(name);
        return Table.Get("name:" + (key.Length > 0 ? key : name.ToLowerInvariant()), name, kind);
    }

    public Group AppGroup(InstalledApp app) => app.IsPackaged
        ? Table.Get("pkg:" + app.PackageFamilyName!.ToLowerInvariant(), app.Name, GroupKind.App, app)
        : Table.Get("app:" + NameKey.Clean(app.Name), app.Name, GroupKind.App, app);

    Frame AppFrame(in Frame parent, string path, InstalledApp app, bool install, Anchor anchor)
        => NewGroupFrame(parent, path, AppGroup(app), anchor, install);

    Frame NewGroupFrame(in Frame parent, string path, Group g, Anchor anchor, bool install) => new()
    {
        Path = path,
        Root = parent.Root.Group == g ? parent.Root : Table.RootFor(g, path, install),
        Anchor = anchor,
        Install = install,
    };

    static Frame Inherit(in Frame parent, string path, Anchor anchor, bool install) => new()
    {
        Path = path,
        Root = parent.Root,
        Anchor = anchor,
        Install = install,
    };

    static Frame FixedFrame(string path, Root root) => new() { Path = path, Root = root, Fixed = true };
}

/// <summary>A compact set of 64-bit file IDs (open addressing), for counting hard links once.</summary>
public sealed class LongSet
{
    long[] slots = new long[1024];
    int count;
    bool hasZero;

    /// <summary>Adds <paramref name="v"/>; false if it was already there.</summary>
    public bool Add(long v)
    {
        if (v == 0)
        {
            if (hasZero) return false;
            hasZero = true;
            return true;
        }
        if ((count + 1) * 2 > slots.Length) Grow();
        if (!Insert(slots, v)) return false;
        count++;
        return true;
    }

    public int Count => count + (hasZero ? 1 : 0);

    static bool Insert(long[] s, long v)
    {
        int mask = s.Length - 1;
        ulong h = unchecked((ulong)v * 0x9E3779B97F4A7C15UL);
        int i = (int)(h ^ (h >> 32)) & mask;
        while (true)
        {
            if (s[i] == 0) { s[i] = v; return true; }
            if (s[i] == v) return false;
            i = (i + 1) & mask;
        }
    }

    void Grow()
    {
        var n = new long[slots.Length * 2];
        foreach (var v in slots)
            if (v != 0) Insert(n, v);
        slots = n;
    }
}
