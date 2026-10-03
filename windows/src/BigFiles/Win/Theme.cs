using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace BigFiles.Win;

/// <summary>Follows the Windows light/dark app setting, including changes while the app is running.</summary>
internal static class Theme
{
    const int WM_SETTINGCHANGE = 0x001A;
    static bool? current;

    public static bool IsDark => current == true;

    public static void Apply()
    {
        bool dark = ReadDark();
        if (current == dark) return;
        current = dark;
        var dicts = Application.Current.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary
        {
            Source = new Uri(dark ? "pack://application:,,,/BigFiles;component/Themes/Dark.xaml" : "pack://application:,,,/BigFiles;component/Themes/Light.xaml"),
        };
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
    }

    static bool ReadDark()
    {
        if (SystemParameters.HighContrast) return false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>Dark or light title bar to match (Windows 10 2004+ / Windows 11).</summary>
    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = IsDark ? 1 : 0;
        if (Native.DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            Native.DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    /// <summary>Call from SourceInitialized: themes the title bar and listens for theme changes.</summary>
    public static void Attach(Window w)
    {
        ApplyTitleBar(w);
        if (HwndSource.FromHwnd(new WindowInteropHelper(w).Handle) is { } src)
            src.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero
                    && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                    Apply();
                return IntPtr.Zero;
            });
    }
}
