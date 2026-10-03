namespace BigFiles.Core;

/// <summary>Folders that must never be moved to the Recycle Bin as a whole.</summary>
public sealed class Protection
{
    readonly Dictionary<string, string> exact = new(PathUtil.Cmp);
    readonly KnownPaths k;

    public Protection(KnownPaths k)
    {
        this.k = k;
        void Add(string? p, string why) { if (p != null) exact.TryAdd(p, why); }

        Add(k.ProgramFiles, "it holds all your installed programs");
        Add(k.ProgramFilesX86, "it holds all your installed programs");
        Add(k.ProgramData, "it holds data of all your installed programs");
        Add(k.UsersRoot, "it holds every user's files");
        Add(k.Profile, "it is your user folder");
        Add(k.AppData, "it holds the data of all your apps");
        Add(k.LocalAppData, "it holds the data of all your apps");
        Add(k.RoamingAppData, "it holds the data of all your apps");
        Add(k.LocalLow, "it holds the data of all your apps");
        Add(PathUtil.Combine(k.LocalAppData, "Programs"), "it holds the apps installed for your account");
        Add(PathUtil.Combine(k.LocalAppData, "Packages"), "it holds the data of all Store apps");
        Add(PathUtil.Combine(k.LocalAppData, "Temp"), "Windows and apps use it while running");
        Add(PathUtil.Combine(k.ProgramFiles, "WindowsApps"), "it holds all Store apps");
        Add(PathUtil.Combine(k.ProgramFiles, "Common Files"), "programs share the files in it");
        Add(PathUtil.Combine(k.ProgramFilesX86, "Common Files"), "programs share the files in it");
        Add(PathUtil.Combine(k.UsersRoot, "Public"), "it is shared by every user");
        Add(PathUtil.Combine(k.UsersRoot, "Default"), "Windows uses it to create new users");
        foreach (var f in k.UserFolders) Add(f, "it is one of your Windows user folders");
        foreach (var name in new[] { "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music", "OneDrive" })
            Add(PathUtil.Combine(k.Profile, name), "it is one of your Windows user folders");
    }

    /// <summary>Why <paramref name="path"/> can't be moved to the Recycle Bin, or null if it can.</summary>
    public string? Reason(string path)
    {
        if (PathUtil.IsDriveRoot(path) || PathUtil.Parent(path) == null) return "it is a whole drive";
        if (PathUtil.IsSameOrUnder(path, k.WindowsDir)) return "it is part of Windows";
        if (exact.TryGetValue(path, out var why)) return why;

        var comps = PathUtil.Components(path);
        if (comps.Length >= 1)
        {
            var top = comps[0];
            if (top.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return "it is the Recycle Bin";
            if (top.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) return "Windows keeps restore points in it";
            if (comps.Length == 1 && Classifier.SystemRootFiles.Contains(top)) return "Windows is using it";
            if (comps.Length == 1 && (top.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
                                      || top.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
                                      || top.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)
                                      || top.Equals("Users", StringComparison.OrdinalIgnoreCase)
                                      || top.Equals("Recovery", StringComparison.OrdinalIgnoreCase)
                                      || top.Equals("Boot", StringComparison.OrdinalIgnoreCase)))
                return "Windows needs it";
        }
        // Other users' profile folders.
        if (PathUtil.Parent(path) is { } parent && PathUtil.Cmp.Equals(parent, k.UsersRoot)) return "it is a user's profile folder";
        return null;
    }
}

public sealed class DeleteTarget
{
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; set; }
}

/// <summary>What "Move to Recycle Bin…" would do with the current selection.</summary>
public sealed class DeletePlan
{
    public List<DeleteTarget> Targets { get; } = new();
    /// <summary>Paths that were refused, with the reason.</summary>
    public List<(string Path, string Reason)> Refused { get; } = new();
    /// <summary>Apps whose install folder was left alone because they have an uninstaller.</summary>
    public List<string> KeptInstalls { get; } = new();
    /// <summary>Other groups that lose data because one of their folders is inside a target.</summary>
    public List<string> AlsoRemoves { get; } = new();
    public long TotalSize => Targets.Sum(t => t.Size);
}

public static class Deletion
{
    /// <summary>Turns selected rows into the list of paths to move to the Recycle Bin.</summary>
    public static DeletePlan Plan(ScanData data, IEnumerable<RowData> selection, Protection protection)
    {
        var plan = new DeletePlan();
        var rows = selection.ToList();
        var groups = rows.Where(r => r.Type == RowType.Group).Select(r => r.Group).Distinct().ToList();
        var groupSet = groups.ToHashSet();
        var candidates = new List<DeleteTarget>();

        foreach (var g in groups)
        {
            bool keepInstall = g.App?.CanUninstall == true;
            bool kept = false;
            foreach (var r in g.VisibleRoots)
            {
                if (keepInstall && r.IsInstall) { kept = true; continue; }
                candidates.Add(new DeleteTarget { Path = r.Path, IsDirectory = !r.IsFile });
            }
            if (kept) plan.KeptInstalls.Add(g.Name);
        }
        // Items whose group is selected too are covered by the group.
        foreach (var r in rows)
            if (r.Item != null && !groupSet.Contains(r.Group))
                candidates.Add(new DeleteTarget { Path = r.Item.Path, IsDirectory = false });

        // Drop duplicates and paths inside another folder that is already being removed.
        var seen = new HashSet<string>(PathUtil.Cmp);
        foreach (var c in candidates.OrderBy(c => c.Path.Length))
        {
            if (!seen.Add(c.Path)) continue;
            if (plan.Targets.Any(t => t.IsDirectory && PathUtil.IsSameOrUnder(c.Path, t.Path))) continue;
            var why = protection.Reason(c.Path);
            if (why != null) { plan.Refused.Add((c.Path, why)); continue; }
            plan.Targets.Add(c);
        }

        var allRoots = data.Table.AllRoots.Where(r => !r.Removed && r.Size > 0).ToList();
        var also = new HashSet<Group>();
        foreach (var t in plan.Targets)
        {
            if (t.IsDirectory)
            {
                foreach (var r in allRoots)
                {
                    if (!PathUtil.IsSameOrUnder(r.Path, t.Path)) continue;
                    t.Size += r.Size;
                    if (!groupSet.Contains(r.Group)) also.Add(r.Group);
                }
            }
            else
            {
                var root = allRoots.FirstOrDefault(r => r.IsFile && PathUtil.Cmp.Equals(r.Path, t.Path));
                t.Size = root?.Size ?? FindItem(data, t.Path)?.Size ?? 0;
            }
        }
        plan.AlsoRemoves.AddRange(also.OrderByDescending(g => g.Size).Select(g => g.Name));
        return plan;
    }

    static Item? FindItem(ScanData data, string path)
    {
        foreach (var g in data.Table.Groups)
            foreach (var i in g.Items)
                if (PathUtil.Cmp.Equals(i.Path, path)) return i;
        return null;
    }

    /// <summary>Updates totals after <paramref name="t"/> was moved to the Recycle Bin (no rescan needed).</summary>
    public static void ApplyRemoved(ScanData data, DeleteTarget t)
    {
        if (t.IsDirectory)
        {
            var removedRoots = new HashSet<Root>();
            foreach (var r in data.Table.AllRoots)
            {
                if (r.Removed || !PathUtil.IsSameOrUnder(r.Path, t.Path)) continue;
                Subtract(data, r, r.Size, r.Files);
                r.Removed = true;
                removedRoots.Add(r);
            }
            foreach (var g in data.Table.Groups)
                g.Items.RemoveAll(i =>
                {
                    if (!PathUtil.IsSameOrUnder(i.Path, t.Path)) return false;
                    // Normally already subtracted with its root; this only matters for odd layouts.
                    if (!removedRoots.Contains(i.Root)) Subtract(data, i.Root, i.Size, 1);
                    return true;
                });
        }
        else
        {
            var fileRoot = data.Table.AllRoots.FirstOrDefault(r => r.IsFile && !r.Removed && PathUtil.Cmp.Equals(r.Path, t.Path));
            if (fileRoot != null)
            {
                Subtract(data, fileRoot, fileRoot.Size, fileRoot.Files);
                fileRoot.Removed = true;
            }
            foreach (var g in data.Table.Groups)
                g.Items.RemoveAll(i =>
                {
                    if (!PathUtil.Cmp.Equals(i.Path, t.Path)) return false;
                    if (fileRoot == null) Subtract(data, i.Root, i.Size, 1);
                    return true;
                });
        }
    }

    static void Subtract(ScanData data, Root r, long size, long files)
    {
        r.Size -= size;
        r.Files -= files;
        r.Group.Size -= size;
        r.Group.Files -= files;
        data.Bytes -= size;
        data.Files -= files;
    }
}
