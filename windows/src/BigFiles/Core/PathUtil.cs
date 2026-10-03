namespace BigFiles.Core;

/// <summary>Windows path helpers that are pure string logic (so they also run in the tests on any OS).</summary>
public static class PathUtil
{
    public static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    /// <summary>Trims quotes and whitespace, uses backslashes, drops a trailing backslash (except on "C:\").</summary>
    public static string? Normalize(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        var s = p.Trim().Trim('"').Trim();
        if (s.Length == 0) return null;
        s = s.Replace('/', '\\');
        bool unc = s.StartsWith(@"\\", StringComparison.Ordinal);
        var parts = s.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        if (unc) return @"\\" + string.Join('\\', parts);
        if (parts[0].Length == 2 && parts[0][1] == ':')
        {
            parts[0] = char.ToUpperInvariant(parts[0][0]) + ":";
            if (parts.Length == 1) return parts[0] + "\\";
        }
        return string.Join('\\', parts);
    }

    public static bool IsDriveRoot(string p) => p.Length == 3 && p[1] == ':' && p[2] == '\\';

    /// <summary>True if <paramref name="path"/> equals <paramref name="dir"/> or lies inside it.</summary>
    public static bool IsSameOrUnder(string path, string dir)
    {
        if (dir.EndsWith('\\')) return path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                                       || Cmp.Equals(path + "\\", dir);
        if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == dir.Length || path[dir.Length] == '\\';
    }

    /// <summary>True if <paramref name="path"/> lies strictly inside <paramref name="dir"/>.</summary>
    public static bool IsUnder(string path, string dir) => path.Length > dir.TrimEnd('\\').Length + 1 && IsSameOrUnder(path, dir);

    public static string Combine(string dir, string name) => dir.EndsWith('\\') ? dir + name : dir + "\\" + name;

    public static string Leaf(string p)
    {
        var t = p.TrimEnd('\\');
        int i = t.LastIndexOf('\\');
        return i < 0 ? t : t[(i + 1)..];
    }

    public static string? Parent(string p)
    {
        if (IsDriveRoot(p)) return null;
        var t = p.TrimEnd('\\');
        int i = t.LastIndexOf('\\');
        if (i < 0) return null;
        if (i == 2 && t[1] == ':') return t[..3];
        return i == 0 ? null : t[..i];
    }

    /// <summary>"C:\" for "C:\x\y"; "\\server\share" for UNC paths.</summary>
    public static string Root(string p)
    {
        if (p.Length >= 2 && p[1] == ':') return char.ToUpperInvariant(p[0]) + ":\\";
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = p[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? @"\\" + parts[0] + "\\" + parts[1] : p;
        }
        return p;
    }

    /// <summary>Path components below the root: "C:\a\b" → ["a", "b"].</summary>
    public static string[] Components(string p)
    {
        var root = Root(p);
        var rest = p.Length > root.Length ? p[root.Length..] : "";
        return rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Drops paths that are inside another path of the list, and duplicates.</summary>
    public static List<string> RemoveNested(IEnumerable<string> paths)
    {
        var sorted = paths.Distinct(Cmp).OrderBy(p => p.Length).ToList();
        var result = new List<string>();
        foreach (var p in sorted)
            if (!result.Any(r => IsSameOrUnder(p, r))) result.Add(p);
        return result;
    }
}

public static class Format
{
    public const long MB = 1024L * 1024;

    /// <summary>Explorer-style sizes (1024-based): "512 KB", "12.3 MB", "1.25 GB".</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return bytes + " bytes";
        string[] units = { "KB", "MB", "GB", "TB", "PB" };
        double v = bytes / 1024.0;
        int u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1024; u++; }
        string num = v < 10 ? v.ToString("0.00") : v < 100 ? v.ToString("0.0") : v.ToString("0");
        return num + " " + units[u];
    }

    public static string Count(long n) => n.ToString("N0");

    public static string Files(long n) => n == 1 ? "1 file" : Count(n) + " files";
}
