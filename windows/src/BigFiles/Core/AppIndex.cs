using System.Text;
using System.Text.RegularExpressions;

namespace BigFiles.Core;

/// <summary>An installed app, from the registry Uninstall keys or the packaged-app (Store) repository.</summary>
public sealed class InstalledApp
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Normalized install folder (InstallLocation, or derived from DisplayIcon/UninstallString).</summary>
    public string? Location { get; set; }
    public string? IconPath { get; init; }
    public int IconIndex { get; init; }
    public string? UninstallString { get; init; }
    public bool SystemComponent { get; init; }
    /// <summary>Set for packaged (MSIX / Store) apps.</summary>
    public string? PackageFamilyName { get; init; }

    public bool IsPackaged => PackageFamilyName != null;
    public bool CanUninstall => IsPackaged || !string.IsNullOrWhiteSpace(UninstallString);

    /// <summary>Higher = better candidate when several entries share a name or folder.</summary>
    internal int Score => (SystemComponent ? 0 : 4) + (UninstallString != null ? 2 : 0) + (IconPath != null ? 1 : 0);

    public override string ToString() => Name;
}

/// <summary>Turns names into comparable keys: lowercase alphanumerics, minus versions / architectures.</summary>
public static class NameKey
{
    public static string Alnum(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    static readonly Regex Brackets = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.Compiled);
    static readonly Regex Version = new(@"^v?\d+([._-]\d+)+[a-z0-9._-]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Lang = new(@"^[a-z]{2}-[a-z]{2}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "x64", "x86", "x32", "amd64", "arm64", "win64", "win32", "64-bit", "32-bit", "64bit", "32bit", "version", "-", "–"
    };

    /// <summary>"Mozilla Firefox (x64 en-US)" → "mozillafirefox"; "7-Zip 23.01 (x64)" → "7zip"; "PyCharm 2024.1" → "pycharm".</summary>
    public static string Clean(string name)
    {
        var s = Brackets.Replace(name, " ");
        var tokens = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                      .Where(t => !Noise.Contains(t) && !Version.IsMatch(t) && !Lang.IsMatch(t));
        var cleaned = Alnum(string.Concat(tokens));
        return cleaned.Length > 0 ? cleaned : Alnum(name);
    }

    /// <summary>Folder names too generic to guess an app from with a fuzzy (suffix) match.</summary>
    public static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "cache", "caches", "data", "userdata", "logs", "log", "temp", "tmp", "update", "updates", "updater",
        "common", "commonfiles", "shared", "packages", "package", "crashdumps", "crashpad", "crashreports",
        "programs", "program", "microsoft", "windows", "config", "settings", "backup", "backups", "bin", "lib",
        "installer", "install", "setup", "downloads", "download", "plugins", "plugin", "default", "profiles",
        "profile", "local", "roaming", "locallow", "app", "apps", "application", "games", "game", "tools",
        "tool", "service", "services", "client", "launcher", "runtime", "driver", "drivers", "assets", "files",
        "store", "storage", "database", "help", "docs", "resources", "content", "studio", "media", "system",
        "support", "web", "user", "users", "public", "home", "desktop", "documents", "music", "pictures",
        "videos", "mail", "sdk", "net", "main", "core", "base", "host", "server", "agent", "manager",
    };

    public static bool AllowFuzzy(string key) => key.Length >= 4 && !Generic.Contains(key);
}

/// <summary>Answers "which installed app does this folder belong to?".</summary>
public sealed class AppIndex
{
    readonly Dictionary<string, InstalledApp> byLocation = new(PathUtil.Cmp);
    readonly Dictionary<string, InstalledApp> byKey = new(StringComparer.Ordinal);
    readonly Dictionary<string, InstalledApp> byPfn = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string Key, InstalledApp App)> keys = new();
    readonly List<(string Loc, InstalledApp App)> locations = new();
    readonly Dictionary<string, InstalledApp?> nameCache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<InstalledApp> Apps { get; }

    public AppIndex(IEnumerable<InstalledApp> apps, IEnumerable<string>? blockedLocations = null, string? windowsDir = null)
    {
        var blocked = new HashSet<string>(blockedLocations ?? Array.Empty<string>(), PathUtil.Cmp);
        var list = new List<InstalledApp>();
        foreach (var a in apps)
        {
            if (string.IsNullOrWhiteSpace(a.Name)) continue;
            var loc = PathUtil.Normalize(a.Location);
            if (loc != null && (blocked.Contains(loc) || PathUtil.IsDriveRoot(loc) || loc.StartsWith(@"\\", StringComparison.Ordinal)
                                || (windowsDir != null && PathUtil.IsSameOrUnder(loc, windowsDir))))
                loc = null;
            a.Location = loc;
            list.Add(a);
        }
        Apps = list;

        foreach (var a in list.OrderByDescending(a => a.Score))
        {
            if (a.Location != null)
            {
                byLocation.TryAdd(a.Location, a);
                locations.Add((a.Location, a));
            }
            if (a.PackageFamilyName != null) byPfn.TryAdd(a.PackageFamilyName, a);
            foreach (var k in new[] { NameKey.Clean(a.Name), NameKey.Alnum(a.Name) }.Distinct())
            {
                if (k.Length == 0) continue;
                if (byKey.TryAdd(k, a)) keys.Add((k, a));
            }
        }
    }

    public IEnumerable<string> InstallLocations => byLocation.Keys;

    /// <summary>The app installed exactly in <paramref name="dir"/>.</summary>
    public InstalledApp? ByLocation(string dir) => byLocation.TryGetValue(dir, out var a) ? a : null;

    /// <summary>
    /// The app installed in <paramref name="dir"/> or in a folder inside it (closest match wins).
    /// With <paramref name="unique"/>, null when several different apps are installed inside it.
    /// </summary>
    public InstalledApp? UnderLocation(string dir, InstalledApp? except = null, bool unique = false)
    {
        InstalledApp? best = null;
        int bestLen = int.MaxValue;
        foreach (var (loc, app) in locations)
        {
            if (app == except || !PathUtil.IsSameOrUnder(loc, dir)) continue;
            if (unique && best != null && best != app) return null;
            if (loc.Length < bestLen) { best = app; bestLen = loc.Length; }
        }
        return best;
    }

    /// <summary>
    /// The app whose name matches a folder name: exact (after removing versions etc.),
    /// or, with <paramref name="allowSuffix"/>, an app name ending with it ("Chrome" → "Google Chrome").
    /// </summary>
    public InstalledApp? ByName(string folderName, bool allowSuffix)
    {
        var cacheKey = (allowSuffix ? "1|" : "0|") + folderName;
        if (nameCache.TryGetValue(cacheKey, out var cached)) return cached;
        var r = Find(folderName, allowSuffix);
        nameCache[cacheKey] = r;
        return r;
    }

    InstalledApp? Find(string folderName, bool allowSuffix)
    {
        var clean = NameKey.Clean(folderName);
        if (clean.Length == 0) return null;
        if (byKey.TryGetValue(clean, out var a)) return a;
        var full = NameKey.Alnum(folderName);
        if (full.Length > 0 && byKey.TryGetValue(full, out a)) return a;
        if (!allowSuffix || !NameKey.AllowFuzzy(clean)) return null;
        InstalledApp? best = null;
        int bestLen = int.MaxValue;
        foreach (var (k, app) in keys)
            if (k.Length > clean.Length && k.EndsWith(clean, StringComparison.Ordinal) && k.Length < bestLen)
            {
                best = app;
                bestLen = k.Length;
            }
        return best;
    }

    public InstalledApp? ByPackageFamily(string pfn) => byPfn.TryGetValue(pfn, out var a) ? a : null;

    /// <summary>"Microsoft.WindowsTerminal_8wekyb3d8bbwe" → "Windows Terminal" (used when the package isn't installed).</summary>
    public static string PrettyPackageName(string pfnOrFullName)
    {
        var name = pfnOrFullName.Split('_')[0];
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var last = parts.LastOrDefault(p => p.Any(char.IsLetter)) ?? name;
        var sb = new StringBuilder();
        for (int i = 0; i < last.Length; i++)
        {
            char c = last[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(last[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString() : pfnOrFullName;
    }

    /// <summary>"Name_1.2.3.0_x64__publisherid" → "Name_publisherid".</summary>
    public static string? FamilyFromFullName(string fullName)
    {
        var parts = fullName.Split('_');
        return parts.Length >= 5 ? parts[0] + "_" + parts[^1] : null;
    }
}
