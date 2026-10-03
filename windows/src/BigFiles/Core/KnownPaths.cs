namespace BigFiles.Core;

/// <summary>The well-known folders of this PC. Built from the real system in Win\SystemInfo, or by hand in tests.</summary>
public sealed class KnownPaths
{
    public required string WindowsDir { get; init; }
    public required string ProgramFiles { get; init; }
    public required string ProgramFilesX86 { get; init; }
    public required string ProgramData { get; init; }
    public required string Profile { get; init; }
    public required string LocalAppData { get; init; }
    public required string RoamingAppData { get; init; }
    /// <summary>Desktop, Documents, Downloads, Pictures, Videos, Music, OneDrive (their real, possibly redirected, paths).</summary>
    public IReadOnlyList<string> UserFolders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FixedDrives { get; init; } = Array.Empty<string>();

    public string UsersRoot => PathUtil.Parent(Profile) ?? Profile;
    public string AppData => PathUtil.Combine(Profile, "AppData");
    public string LocalLow => PathUtil.Combine(AppData, "LocalLow");
    public string SystemDrive => PathUtil.Root(WindowsDir);

    /// <summary>"C:\Users\me\Documents" → "~\Documents".</summary>
    public string Pretty(string path)
    {
        if (PathUtil.IsSameOrUnder(path, Profile)) return "~" + path[Profile.Length..];
        return path;
    }

    /// <summary>Folders that can never be an app's install location (bad registry entries point at them).</summary>
    public IEnumerable<string> NotAppLocations()
    {
        yield return WindowsDir;
        yield return ProgramFiles;
        yield return ProgramFilesX86;
        yield return ProgramData;
        yield return UsersRoot;
        yield return Profile;
        yield return AppData;
        yield return LocalAppData;
        yield return RoamingAppData;
        yield return LocalLow;
        yield return PathUtil.Combine(LocalAppData, "Programs");
        yield return PathUtil.Combine(LocalAppData, "Packages");
        yield return PathUtil.Combine(LocalAppData, "Temp");
        yield return PathUtil.Combine(ProgramFiles, "Common Files");
        yield return PathUtil.Combine(ProgramFilesX86, "Common Files");
        yield return PathUtil.Combine(ProgramFiles, "WindowsApps");
        foreach (var f in UserFolders) yield return f;
    }
}
