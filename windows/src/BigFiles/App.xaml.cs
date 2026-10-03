using System.Windows;
using BigFiles.Win;

namespace BigFiles;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.Apply();

        // "--scan apps|pc|folder <path>" is passed when restarting as administrator, to scan the same thing again.
        ScanScope? scope = null;
        string? folder = null;
        var a = e.Args;
        if (a.Length >= 2 && a[0] == "--scan")
        {
            scope = a[1] switch { "pc" => ScanScope.WholePc, "folder" => ScanScope.Folder, _ => ScanScope.AppsAndUser };
            if (scope == ScanScope.Folder) folder = a.Length >= 3 ? a[2] : null;
            if (scope == ScanScope.Folder && folder == null) scope = null;
        }

        var w = new MainWindow(scope, folder);
        MainWindow = w;
        w.Show();
    }
}
