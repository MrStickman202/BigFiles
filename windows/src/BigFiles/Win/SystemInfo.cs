using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using BigFiles.Core;
using Microsoft.Win32;

namespace BigFiles.Win;

public enum ScanScope { AppsAndUser, WholePc, Folder }

/// <summary>Reads this PC's well-known folders and installed apps.</summary>
internal static class SystemInfo
{
    static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public static KnownPaths Known()
    {
        static string F(Environment.SpecialFolder f) =>
            PathUtil.Normalize(Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify)) ?? "";

        var profile = F(Environment.SpecialFolder.UserProfile);
        var userFolders = new List<string?>
        {
            F(Environment.SpecialFolder.DesktopDirectory),
            F(Environment.SpecialFolder.MyDocuments),
            KnownFolder(FolderIdDownloads) ?? PathUtil.Combine(profile, "Downloads"),
            F(Environment.SpecialFolder.MyPictures),
            F(Environment.SpecialFolder.MyVideos),
            F(Environment.SpecialFolder.MyMusic),
        };
        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            userFolders.Add(PathUtil.Normalize(Environment.GetEnvironmentVariable(v)));

        var drives = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed && d.IsReady && PathUtil.Normalize(d.RootDirectory.FullName) is { } r)
                    drives.Add(r);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var windows = F(Environment.SpecialFolder.Windows);
        return new KnownPaths
        {
            WindowsDir = windows.Length > 0 ? windows : @"C:\Windows",
            ProgramFiles = F(Environment.SpecialFolder.ProgramFiles),
            ProgramFilesX86 = F(Environment.SpecialFolder.ProgramFilesX86),
            ProgramData = F(Environment.SpecialFolder.CommonApplicationData),
            Profile = profile,
            LocalAppData = F(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData = F(Environment.SpecialFolder.ApplicationData),
            UserFolders = userFolders.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).Distinct(PathUtil.Cmp).ToList(),
            FixedDrives = drives,
        };
    }

    static string? KnownFolder(Guid id)
    {
        try
        {
            if (Native.SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
            try { return PathUtil.Normalize(Marshal.PtrToStringUni(p)); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch { return null; }
    }

    // ---------- Installed apps ----------

    public static List<InstalledApp> LoadApps(KnownPaths k)
    {
        var list = new List<InstalledApp>();
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view, label) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"),
                     (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32"),
                     (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU"),
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(uninstall);
                if (root != null) ReadUninstall(root, label, k, list);
            }
            catch { /* unreadable hive: skip */ }
        }
        try { ReadPackages(list); } catch { /* no packaged apps */ }
        return list;
    }

    static void ReadUninstall(RegistryKey root, string label, KnownPaths k, List<InstalledApp> list)
    {
        foreach (var sub in root.GetSubKeyNames())
        {
            try
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                if (key.GetValue("ParentKeyName") != null) continue; // updates of another entry
                if (key.GetValue("ReleaseType") is string rt && (rt.Contains("Update", StringComparison.OrdinalIgnoreCase) || rt.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var (iconPath, iconIndex) = ParseIcon(Expand(key.GetValue("DisplayIcon") as string));
                var uninstallString = (key.GetValue("UninstallString") as string)?.Trim();
                if (string.IsNullOrEmpty(uninstallString)) uninstallString = null;
                var location = PathUtil.Normalize(Expand(key.GetValue("InstallLocation") as string))
                               ?? DeriveLocation(iconPath, uninstallString, k);

                list.Add(new InstalledApp
                {
                    Id = label + "\\" + sub,
                    Name = name.Trim(),
                    Location = location,
                    IconPath = iconPath,
                    IconIndex = iconIndex,
                    UninstallString = uninstallString,
                    SystemComponent = key.GetValue("SystemComponent") is int sc && sc == 1,
                });
            }
            catch { /* one broken entry shouldn't hide the others */ }
        }
    }

    static string? Expand(string? s) => string.IsNullOrWhiteSpace(s) ? null : Environment.ExpandEnvironmentVariables(s.Trim());

    /// <summary>"\"C:\x\app.exe\",0" → (C:\x\app.exe, 0).</summary>
    internal static (string? Path, int Index) ParseIcon(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, 0);
        s = s.Trim();
        string path;
        string rest = "";
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end < 0) return (null, 0);
            path = s[1..end];
            rest = s[(end + 1)..];
        }
        else
        {
            int comma = s.LastIndexOf(',');
            if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _))
            {
                path = s[..comma];
                rest = s[comma..];
            }
            else path = s;
        }
        int index = 0;
        var r = rest.Trim().TrimStart(',').Trim();
        if (r.Length > 0) int.TryParse(r, out index);
        path = path.Trim();
        return path.Length > 0 && File.Exists(path) ? (path, index) : (null, 0);
    }

    /// <summary>Install folder of an app without InstallLocation: where its icon or uninstaller lives.</summary>
    static string? DeriveLocation(string? iconPath, string? uninstall, KnownPaths k)
    {
        foreach (var file in new[] { iconPath, ParseCommand(uninstall).File })
        {
            if (string.IsNullOrEmpty(file) || !Path.IsPathRooted(file)) continue;
            var dir = PathUtil.Normalize(Path.GetDirectoryName(file));
            if (dir == null || PathUtil.IsSameOrUnder(dir, k.WindowsDir)) continue;
            if (dir.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)
                || dir.Contains(@"\Installer", StringComparison.OrdinalIgnoreCase)
                || dir.Contains(@"\{", StringComparison.Ordinal))
                continue;
            var leaf = PathUtil.Leaf(dir).ToLowerInvariant();
            if (leaf is "uninst" or "uninstall" or "uninstaller" or "_uninst" or "bin" or "bin64" or "x64" or "x86")
                dir = PathUtil.Parent(dir) ?? dir;
            return dir;
        }
        return null;
    }

    /// <summary>Splits a command line into the program and its arguments (handles unquoted paths with spaces).</summary>
    internal static (string? File, string Args) ParseCommand(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return (null, "");
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        // Unquoted: try the longest prefix ending in .exe that exists ("C:\Program Files\App\uninst.exe /S").
        int idx = 0;
        while ((idx = cmd.IndexOf(".exe", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            idx += 4;
            var candidate = cmd[..idx];
            if (idx == cmd.Length || cmd[idx] == ' ')
                if (File.Exists(candidate) || !candidate.Contains('\\'))
                    return (candidate, cmd[idx..].Trim());
        }
        int space = cmd.IndexOf(' ');
        return space < 0 ? (cmd, "") : (cmd[..space], cmd[(space + 1)..]);
    }

    static void ReadPackages(List<InstalledApp> list)
    {
        using var root = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (root == null) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var full in root.GetSubKeyNames())
        {
            var parts = full.Split('_');
            if (parts.Length < 5 || parts[3].Length != 0) continue; // skip resource packages
            var pfn = AppIndex.FamilyFromFullName(full);
            if (pfn == null || !seen.Add(pfn)) continue;
            try
            {
                using var key = root.OpenSubKey(full);
                var name = ResolveIndirect(key?.GetValue("DisplayName") as string) ?? AppIndex.PrettyPackageName(pfn);
                list.Add(new InstalledApp
                {
                    Id = "pkg\\" + pfn,
                    Name = name,
                    Location = PathUtil.Normalize(key?.GetValue("PackageRootFolder") as string),
                    PackageFamilyName = pfn,
                });
            }
            catch { }
        }
    }

    static string? ResolveIndirect(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.StartsWith('@'))
        {
            var buf = new char[512];
            if (Native.SHLoadIndirectString(s, buf, buf.Length, IntPtr.Zero) != 0) return null;
            int len = Array.IndexOf(buf, '\0');
            s = new string(buf, 0, len < 0 ? buf.Length : len);
        }
        s = s.Trim();
        return s.Length == 0 || s.StartsWith('@') || s.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    // ---------- Scopes ----------

    public static List<string> ScanRoots(ScanScope scope, string? folder, KnownPaths k, AppIndex apps)
    {
        switch (scope)
        {
            case ScanScope.WholePc:
                return k.FixedDrives.Count > 0 ? k.FixedDrives.ToList() : new List<string> { k.SystemDrive };
            case ScanScope.Folder:
                return folder != null ? new List<string> { folder } : new List<string>();
            default:
            {
                var roots = new List<string> { k.ProgramFiles, k.ProgramFilesX86, k.ProgramData, k.Profile }
                    .Where(p => p.Length > 0 && Directory.Exists(p)).ToList();
                // Apps installed elsewhere (C:\Riot Games\League of Legends, D:\SteamLibrary\…) are apps too.
                foreach (var loc in apps.InstallLocations)
                {
                    if (roots.Any(r => PathUtil.IsSameOrUnder(loc, r)) || PathUtil.IsSameOrUnder(loc, k.WindowsDir)) continue;
                    if (!k.FixedDrives.Any(d => PathUtil.IsSameOrUnder(loc, d))) continue;
                    if (Directory.Exists(loc)) roots.Add(loc);
                }
                return PathUtil.RemoveNested(roots);
            }
        }
    }

    // ---------- Elevation ----------

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>Starts this exe again as administrator. False if the user said no.</summary>
    public static bool RestartElevated(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Native.ERROR_CANCELLED)
        {
            return false;
        }
    }
}
