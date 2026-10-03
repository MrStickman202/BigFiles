using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BigFiles.Core;

namespace BigFiles.Win;

/// <summary>Explorer, Recycle Bin and uninstaller actions.</summary>
internal static class Shell
{
    public static void ShowInExplorer(string path, bool isDirectory)
    {
        if (isDirectory) Start("explorer.exe", Quote(path));
        else Start("explorer.exe", "/select," + Quote(path));
    }

    public static void Open(string path) => Start(path, null);

    static string Quote(string p) => "\"" + p + "\"";

    static void Start(string file, string? args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = true };
            if (args != null) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Native.ERROR_CANCELLED) { }
    }

    /// <summary>
    /// Moves one file or folder to the Recycle Bin (never deletes permanently: if something is too big
    /// for the Recycle Bin, Windows asks first because of FOF_WANTNUKEWARNING).
    /// Returns true if the path is gone afterwards.
    /// </summary>
    public static bool MoveToRecycleBin(IntPtr owner, string path, out string? error)
    {
        error = null;
        var op = new Native.SHFILEOPSTRUCTW
        {
            hwnd = owner,
            wFunc = Native.FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_WANTNUKEWARNING | Native.FOF_NOCONFIRMMKDIR,
        };
        int rc = Native.SHFileOperationW(ref op);
        bool gone = !File.Exists(path) && !Directory.Exists(path);
        if (!gone)
            error = op.fAnyOperationsAborted != 0 ? "cancelled" : rc != 0 ? $"Windows error 0x{rc:X}" : "some files are in use";
        return gone;
    }

    /// <summary>Runs an app's own uninstaller (or opens Settings › Apps for Store apps).</summary>
    public static string? Uninstall(InstalledApp app)
    {
        try
        {
            if (app.IsPackaged)
            {
                Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
                return null;
            }
            var cmd = app.UninstallString!;
            if (cmd.Contains("://") && !cmd.Contains('\\'))
            {
                Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true });
                return null;
            }
            var (file, args) = SystemInfo.ParseCommand(cmd);
            if (file == null) return "The uninstall command is empty.";
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Native.ERROR_CANCELLED)
        {
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    public static void CopyText(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (COMException) { Thread.Sleep(50); } // clipboard busy
        }
    }
}

/// <summary>Small 16 px icons for rows. Never reads the scanned files themselves (icons come from the extension).</summary>
internal static class Icons
{
    static readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Folder => Cached("::folder", () => FromShell("folder", Native.FILE_ATTRIBUTE_DIRECTORY));

    public static ImageSource? ForExtension(string name)
    {
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext)) ext = ".file";
        return Cached("ext:" + ext, () => FromShell("x" + ext, Native.FILE_ATTRIBUTE_NORMAL));
    }

    public static ImageSource? ForApp(InstalledApp app)
    {
        if (app.IconPath == null) return Folder;
        return Cached($"app:{app.IconPath},{app.IconIndex}", () => Extract(app.IconPath, app.IconIndex)) ?? Folder;
    }

    /// <summary>The real icon of a Windows user folder (Documents, Downloads…).</summary>
    public static ImageSource? ForKnownFolder(string path) =>
        Cached("kf:" + path, () =>
        {
            var info = new Native.SHFILEINFOW();
            var r = Native.SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFOW>(), Native.SHGFI_ICON | Native.SHGFI_SMALLICON);
            return r == IntPtr.Zero ? null : FromHIcon(info.hIcon);
        }) ?? Folder;

    public static ImageSource? RecycleBin => Cached("::recycle", () =>
    {
        var info = new Native.SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<Native.SHSTOCKICONINFO>() };
        return Native.SHGetStockIconInfo(Native.SIID_RECYCLERFULL, Native.SHGSI_ICON | Native.SHGSI_SMALLICON, ref info) == 0
            ? FromHIcon(info.hIcon) : null;
    }) ?? Folder;

    static ImageSource? Cached(string key, Func<ImageSource?> make)
    {
        if (cache.TryGetValue(key, out var img)) return img;
        try { img = make(); }
        catch { img = null; }
        cache[key] = img;
        return img;
    }

    static ImageSource? FromShell(string name, uint attributes)
    {
        var info = new Native.SHFILEINFOW();
        var r = Native.SHGetFileInfoW(name, attributes, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFOW>(),
                                      Native.SHGFI_ICON | Native.SHGFI_SMALLICON | Native.SHGFI_USEFILEATTRIBUTES);
        return r == IntPtr.Zero ? null : FromHIcon(info.hIcon);
    }

    static ImageSource? Extract(string file, int index)
    {
        var small = new IntPtr[1];
        var large = new IntPtr[1];
        if (Native.ExtractIconExW(file, index, large, small, 1) == 0) return null;
        var use = small[0] != IntPtr.Zero ? small[0] : large[0];
        var other = use == small[0] ? large[0] : small[0];
        if (other != IntPtr.Zero) Native.DestroyIcon(other);
        return FromHIcon(use);
    }

    static ImageSource? FromHIcon(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
            src.Freeze();
            return src;
        }
        finally
        {
            Native.DestroyIcon(h);
        }
    }
}
