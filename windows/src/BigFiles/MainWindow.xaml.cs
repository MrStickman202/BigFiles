using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BigFiles.Core;
using BigFiles.Views;
using BigFiles.Win;

namespace BigFiles;

public partial class MainWindow : Window
{
    static readonly (string Label, long Bytes)[] MinSizes =
    {
        ("≥ 1 MB", 1 * Format.MB), ("≥ 10 MB", 10 * Format.MB), ("≥ 50 MB", 50 * Format.MB), ("≥ 100 MB", 100 * Format.MB),
        ("≥ 500 MB", 500 * Format.MB), ("≥ 1 GB", 1024 * Format.MB), ("≥ 5 GB", 5 * 1024 * Format.MB),
    };

    // Scan state
    ScanScope scope = ScanScope.AppsAndUser;
    string? folder;
    volatile int scanId;
    volatile Scanner? currentScanner;
    volatile bool stopRequested;
    bool scanning;
    readonly Stopwatch scanClock = new();
    readonly DispatcherTimer progressTimer;
    readonly ScanScope? startupScope;
    readonly string? startupFolder;

    // Results
    ScanData? data;
    Protection? protection;
    ObservableCollection<RowVM> rows = new();
    readonly HashSet<string> expanded = new(StringComparer.Ordinal);

    // View options
    long minSize = 50 * Format.MB;
    SortColumn sort = SortColumn.Size;
    bool descending = true;
    readonly DispatcherTimer searchTimer;
    bool suppressScopeEvents;

    public MainWindow(ScanScope? startupScope = null, string? startupFolder = null)
    {
        this.startupScope = startupScope;
        this.startupFolder = startupFolder;
        InitializeComponent();

        foreach (var (label, bytes) in MinSizes)
            MinSizeBox.Items.Add(new ComboBoxItem { Content = label, Tag = bytes });
        MinSizeBox.SelectedIndex = 2;

        suppressScopeEvents = true;
        ScopeBox.SelectedIndex = 0;
        suppressScopeEvents = false;

        progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => ShowProgress(), Dispatcher);
        progressTimer.Stop();
        searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            searchTimer!.Stop();
            Rebuild();
        }, Dispatcher);
        searchTimer.Stop();

        EmptyIcon.Source = LoadAppIcon(128);
        Tree.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(Header_Click));
        UpdateHeaders();
        UpdateScanButton();

        SourceInitialized += (_, _) => Theme.Attach(this);
        PreviewKeyDown += Window_PreviewKeyDown;
        Loaded += (_, _) =>
        {
            if (startupScope != null) StartScan(startupScope.Value, startupFolder);
        };
    }

    static ImageSource? LoadAppIcon(int size)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/BigFiles;component/Assets/BigFiles.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).First();
        }
        catch { return null; }
    }

    // ============================== Scanning ==============================

    void StartScan(ScanScope newScope, string? newFolder = null)
    {
        currentScanner?.Cancel();
        int id = ++scanId;
        stopRequested = false;
        scope = newScope;
        folder = newScope == ScanScope.Folder ? newFolder : null;
        SelectScopeItem();

        // A new scan starts from a clean slate: no results, nothing selected or expanded.
        data = null;
        Tree.SelectedItems.Clear();
        expanded.Clear();
        rows = new ObservableCollection<RowVM>();
        Tree.ItemsSource = rows;
        EmptyState.Visibility = Visibility.Collapsed;
        Tree.Visibility = Visibility.Visible;
        ShowMessage("Scanning…", "Results appear when the scan finishes. Press Stop to see what was found so far.");

        scanning = true;
        scanClock.Restart();
        UpdateScanButton();
        StartProgressAnimation();
        ShowProgress();
        progressTimer.Start();

        var s = newScope;
        var f = folder;
        var thread = new Thread(() =>
        {
            ScanData? result = null;
            Exception? error = null;
            try
            {
                var known = SystemInfo.Known();
                var apps = new AppIndex(SystemInfo.LoadApps(known), known.NotAppLocations(), known.WindowsDir);
                var classifier = new Classifier(known, apps);
                var roots = SystemInfo.ScanRoots(s, f, known, apps);
                var scanner = new Scanner(classifier, new NativeFileSystem());
                if (id == scanId) currentScanner = scanner;
                if (id != scanId || stopRequested) scanner.Cancel();
                result = scanner.Run(roots);
            }
            catch (Exception e)
            {
                error = e;
            }
            Dispatcher.BeginInvoke(() => ScanFinished(id, result, error));
        })
        {
            IsBackground = true,
            Name = "BigFiles scan",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    void StopScan()
    {
        stopRequested = true;
        currentScanner?.Cancel();
    }

    void ScanFinished(int id, ScanData? result, Exception? error)
    {
        if (id != scanId) return; // an older scan that was replaced
        scanning = false;
        currentScanner = null;
        scanClock.Stop();
        progressTimer.Stop();
        StopProgressAnimation();
        UpdateScanButton();

        if (error != null || result == null)
        {
            ShowMessage("The scan failed", error?.Message ?? "Unknown error");
            StatusMain.Text = "Scan failed";
            StatusMuted.Text = "";
            return;
        }
        data = result;
        protection = new Protection(result.Known);
        Rebuild();
    }

    void ShowProgress()
    {
        if (!scanning) return;
        var sc = currentScanner;
        if (sc == null)
        {
            StatusMain.Text = "Reading installed apps…";
            StatusMuted.Text = "";
        }
        else
        {
            StatusMain.Text = $"Scanning… {Format.Count(sc.Files)} files · {Format.Size(sc.Bytes)}";
            StatusMuted.Text = "   " + sc.CurrentPath;
        }
        StatusGap.Text = "";
        ErrorsText.Text = "";
    }

    void StartProgressAnimation()
    {
        ProgressTrack.Visibility = Visibility.Visible;
        double w = Math.Max(ProgressTrack.ActualWidth, 400);
        ProgressMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-160, w, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    void StopProgressAnimation()
    {
        ProgressMove.BeginAnimation(TranslateTransform.XProperty, null);
        ProgressTrack.Visibility = Visibility.Collapsed;
    }

    void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (scanning) StartProgressAnimation();
    }

    void UpdateScanButton()
    {
        ScanText.Text = scanning ? "Stop" : data == null && scanId == 0 ? "Scan" : "Rescan";
        ScanIcon.Text = scanning ? "" : "";
        ScanButton.ToolTip = scanning ? "Stop and show what was found so far (Esc)" : "Scan again (F5)";
    }

    void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (scanning) StopScan();
        else StartScan(scope, folder);
    }

    void EmptyScanApps_Click(object sender, RoutedEventArgs e) => StartScan(ScanScope.AppsAndUser);
    void EmptyScanPc_Click(object sender, RoutedEventArgs e) => StartScan(ScanScope.WholePc);
    void EmptyChoose_Click(object sender, RoutedEventArgs e) => ChooseFolder();

    void ChooseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to scan" };
        if (dlg.ShowDialog(this) == true && PathUtil.Normalize(dlg.FolderName) is { } path)
            StartScan(ScanScope.Folder, path);
        else
            SelectScopeItem();
    }

    /// <summary>Shows the current scope in the dropdown (adds an item for a chosen folder).</summary>
    void SelectScopeItem()
    {
        suppressScopeEvents = true;
        var existing = ScopeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == "folder");
        if (scope == ScanScope.Folder && folder != null)
        {
            if (existing == null)
            {
                existing = new ComboBoxItem { Tag = "folder" };
                ScopeBox.Items.Insert(2, existing);
            }
            existing.Content = "Folder: " + PathUtil.Leaf(folder);
            existing.ToolTip = folder;
            ScopeBox.SelectedItem = existing;
        }
        else
        {
            ScopeBox.SelectedIndex = scope == ScanScope.WholePc ? 1 : 0;
        }
        suppressScopeEvents = false;
    }

    void ScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressScopeEvents || ScopeBox.SelectedItem is not ComboBoxItem item) return;
        switch ((string)item.Tag)
        {
            case "apps": StartScan(ScanScope.AppsAndUser); break;
            case "pc": StartScan(ScanScope.WholePc); break;
            case "folder": if (folder != null) StartScan(ScanScope.Folder, folder); break;
            case "choose": Dispatcher.BeginInvoke(ChooseFolder); break;
        }
    }

    // ============================== Building the tree ==============================

    ViewOptions Options => new() { MinSize = minSize, Search = SearchBox.Text, Sort = sort, Descending = descending };

    bool IsExpanded(RowData r, ViewOptions o)
    {
        if (expanded.Contains(r.Key)) return true;
        // While searching, open groups that match only through their files, so the files are visible.
        return o.Search.Trim().Length > 0 && r.Type == RowType.Group && data != null
               && !TreeBuilder.GroupMatches(r.Group, data.Known, o.Search.Trim());
    }

    void Rebuild()
    {
        if (data == null) return;
        var o = Options;
        var selectedKeys = Tree.SelectedItems.Cast<RowVM>().Select(r => r.Key).ToHashSet();
        var list = new List<RowVM>();
        foreach (var g in TreeBuilder.Groups(data, o))
        {
            bool open = IsExpanded(g, o);
            var vm = new RowVM(g, data.Known, open);
            list.Add(vm);
            if (open) AddChildren(list, vm, o);
        }
        rows = new ObservableCollection<RowVM>(list);
        Tree.ItemsSource = rows;
        foreach (var r in rows)
            if (selectedKeys.Contains(r.Key)) Tree.SelectedItems.Add(r);

        if (rows.Count == 0)
            ShowMessage(o.Search.Trim().Length > 0 ? "Nothing matches your search" : $"Nothing is {Format.Size(minSize)} or bigger",
                        o.Search.Trim().Length > 0 ? "Try another name, or a smaller minimum size." : "Choose a smaller minimum size in the toolbar.");
        else
            HideMessage();
        UpdateStatus();
    }

    void AddChildren(List<RowVM> list, RowVM parent, ViewOptions o)
    {
        foreach (var c in TreeBuilder.Children(parent.Data, data!, o))
        {
            bool open = c.Expandable && expanded.Contains(c.Key);
            var vm = new RowVM(c, data!.Known, open);
            list.Add(vm);
            if (open) AddChildren(list, vm, o);
        }
    }

    void Toggle(RowVM vm)
    {
        if (!vm.Data.Expandable || data == null) return;
        int index = rows.IndexOf(vm);
        if (index < 0) return;
        if (vm.IsExpanded)
        {
            vm.IsExpanded = false;
            expanded.Remove(vm.Key);
            while (index + 1 < rows.Count && rows[index + 1].Level > vm.Level) rows.RemoveAt(index + 1);
        }
        else
        {
            vm.IsExpanded = true;
            expanded.Add(vm.Key);
            var children = new List<RowVM>();
            AddChildren(children, vm, Options);
            for (int i = 0; i < children.Count; i++) rows.Insert(index + 1 + i, children[i]);
        }
    }

    void Toggle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RowVM vm) Toggle(vm);
        e.Handled = true;
    }

    static bool FromToggle(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { Tag: "toggle" }) return true;
            if (d is ListViewItem) return false;
        }
        return false;
    }

    void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || FromToggle(e.OriginalSource)) return;
        if ((sender as ListViewItem)?.DataContext is not RowVM vm) return;
        if (vm.Data.Type is RowType.Smaller or RowType.Remainder) Toggle(vm);
        else ShowInExplorer(new[] { vm });
        e.Handled = true;
    }

    void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string tag }) return;
        var col = Enum.Parse<SortColumn>(tag);
        if (col == sort) descending = !descending;
        else
        {
            sort = col;
            descending = col == SortColumn.Size;
        }
        UpdateHeaders();
        Rebuild();
    }

    void UpdateHeaders()
    {
        foreach (var c in ((GridView)Tree.View).Columns)
        {
            if (c.Header is not GridViewColumnHeader { Tag: string tag } h) continue;
            h.Content = tag + (Enum.Parse<SortColumn>(tag) == sort ? (descending ? "  ▾" : "  ▴") : "");
        }
    }

    void Tree_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Let the Location column take the remaining width.
        double used = NameCol.ActualWidth + SizeCol.ActualWidth + KindCol.ActualWidth + 40;
        LocationCol.Width = Math.Max(160, Tree.ActualWidth - used);
    }

    void MinSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MinSizeBox.SelectedItem is ComboBoxItem { Tag: long bytes })
        {
            minSize = bytes;
            Rebuild();
        }
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        searchTimer.Stop();
        searchTimer.Start();
    }

    void ShowMessage(string title, string body)
    {
        MessageTitle.Text = title;
        MessageBody.Text = body;
        Message.Visibility = Visibility.Visible;
    }

    void HideMessage() => Message.Visibility = Visibility.Collapsed;

    void UpdateStatus()
    {
        if (data == null) return;
        int groups = rows.Count(r => r.Data.Type == RowType.Group);
        var elapsed = scanClock.Elapsed.TotalSeconds;
        StatusMain.Text = $"{Format.Count(groups)} apps & folders · total of {Format.Count(data.Files)} files ({Format.Size(data.Bytes)}) · {elapsed:0.0} s";
        StatusMuted.Text = data.Cancelled ? "   (stopped early — showing what was found so far)" : "";
        if (data.Errors > 0)
        {
            var text = $"{Format.Count(data.Errors)} location{(data.Errors == 1 ? "" : "s")} couldn't be read";
            if (SystemInfo.IsElevated)
            {
                StatusGap.Text = "   ·   " + text;
                ErrorsText.Text = "";
            }
            else
            {
                StatusGap.Text = "   ·   ";
                ErrorsText.Text = text + " — restart as administrator";
            }
        }
        else
        {
            StatusGap.Text = "";
            ErrorsText.Text = "";
        }
    }

    void ErrorsLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ErrorsText.Text)) return;
        var args = scope switch
        {
            ScanScope.WholePc => "--scan pc",
            ScanScope.Folder when folder != null => $"--scan folder \"{folder}\"",
            _ => "--scan apps",
        };
        if (SystemInfo.RestartElevated(args)) Close();
    }

    // ============================== Keyboard ==============================

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && !scanning)
        {
            StartScan(scope, folder);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (scanning) StopScan();
            else if (SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0) SearchBox.Clear();
            else return;
            e.Handled = true;
        }
    }

    void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var sel = SelectedRows();
        var focused = (Keyboard.FocusedElement as ListViewItem)?.DataContext as RowVM ?? sel.FirstOrDefault();
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Delete:
                Recycle(sel);
                break;
            case Key.Enter:
                if (focused != null && sel.Count == 1 && focused.Data.Type is RowType.Smaller or RowType.Remainder) Toggle(focused);
                else ShowInExplorer(sel);
                break;
            case Key.C when ctrl:
                CopyPaths(sel);
                break;
            case Key.Right when focused != null:
                if (focused.Data.Expandable && !focused.IsExpanded) Toggle(focused);
                break;
            case Key.Left when focused != null:
                if (focused.Data.Expandable && focused.IsExpanded) Toggle(focused);
                else SelectParent(focused);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void SelectParent(RowVM vm)
    {
        int i = rows.IndexOf(vm);
        while (--i >= 0)
        {
            if (rows[i].Level >= vm.Level) continue;
            Tree.SelectedItems.Clear();
            Tree.SelectedItem = rows[i];
            (Tree.ItemContainerGenerator.ContainerFromItem(rows[i]) as ListViewItem)?.Focus();
            return;
        }
    }

    // ============================== Actions ==============================

    List<RowVM> SelectedRows() => Tree.SelectedItems.Cast<RowVM>().ToList();

    /// <summary>The real paths behind rows: a group stands for its roots, a file for itself.</summary>
    static IEnumerable<(string Path, bool IsDir)> Targets(IEnumerable<RowVM> sel)
    {
        var list = sel.ToList();
        var groups = list.Where(r => r.Data.Type == RowType.Group).Select(r => r.Data.Group).ToHashSet();
        var seen = new HashSet<string>(PathUtil.Cmp);
        foreach (var r in list)
        {
            if (r.Data.Type == RowType.Group)
            {
                foreach (var root in r.Data.Group.VisibleRoots.OrderByDescending(x => x.Size))
                    if (seen.Add(root.Path)) yield return (root.Path, !root.IsFile);
            }
            else if (r.Data.Item is { } item && !groups.Contains(item.Group) && seen.Add(item.Path))
                yield return (item.Path, false);
        }
    }

    void ShowInExplorer(IEnumerable<RowVM> sel)
    {
        foreach (var (path, isDir) in Targets(sel).Take(10)) Shell.ShowInExplorer(path, isDir);
    }

    void OpenItems(IEnumerable<RowVM> sel)
    {
        foreach (var (path, isDir) in Targets(sel).Take(10))
        {
            if (isDir) Shell.ShowInExplorer(path, true);
            else Shell.Open(path);
        }
    }

    void CopyPaths(IEnumerable<RowVM> sel)
    {
        var text = string.Join(Environment.NewLine, Targets(sel).Select(t => t.Path));
        if (text.Length > 0) Shell.CopyText(text);
    }

    void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var sel = SelectedRows();
        bool any = Targets(sel).Any();
        if (!any)
        {
            e.Handled = true; // nothing to act on (e.g. only "Smaller files" rows)
            return;
        }
        MenuShow.IsEnabled = MenuOpen.IsEnabled = MenuCopy.IsEnabled = MenuRecycle.IsEnabled = true;
        var app = UninstallableApp(sel);
        MenuUninstall.Visibility = app != null ? Visibility.Visible : Visibility.Collapsed;
        if (app != null) MenuUninstall.Header = app.IsPackaged ? $"Uninstall {app.Name}… (opens Settings)" : $"Uninstall {app.Name}…";
    }

    static InstalledApp? UninstallableApp(List<RowVM> sel) =>
        sel.Count == 1 && sel[0].Data.Type == RowType.Group && sel[0].Data.Group.App is { CanUninstall: true } app ? app : null;

    void MenuShow_Click(object sender, RoutedEventArgs e) => ShowInExplorer(SelectedRows());
    void MenuOpen_Click(object sender, RoutedEventArgs e) => OpenItems(SelectedRows());
    void MenuCopy_Click(object sender, RoutedEventArgs e) => CopyPaths(SelectedRows());
    void MenuRecycle_Click(object sender, RoutedEventArgs e) => Recycle(SelectedRows());

    void MenuUninstall_Click(object sender, RoutedEventArgs e)
    {
        var app = UninstallableApp(SelectedRows());
        if (app == null) return;
        bool ok = Dialog.Confirm(this, "Uninstall", $"Uninstall {app.Name}?",
            app.IsPackaged
                ? "This opens Settings › Apps, where you can uninstall it."
                : "This runs the app's own uninstaller. When it has finished, press Rescan to update the list.",
            null, Array.Empty<(string, bool)>(), app.IsPackaged ? "Open Settings" : "Uninstall", danger: !app.IsPackaged);
        if (!ok) return;
        var error = Shell.Uninstall(app);
        if (error != null) Dialog.Info(this, "Uninstall", $"Couldn't start the uninstaller for {app.Name}", error);
    }

    void Recycle(List<RowVM> sel)
    {
        if (data == null || protection == null || sel.Count == 0 || scanning) return;
        var known = data.Known;
        var plan = Deletion.Plan(data, sel.Select(r => r.Data), protection);

        var notes = new List<(string, bool)>();
        foreach (var (path, reason) in plan.Refused)
            notes.Add(($"Not included: “{known.Pretty(path)}” is protected because {reason}. You can expand it and move the files inside it to the Recycle Bin instead.", false));
        foreach (var name in plan.KeptInstalls)
            notes.Add(($"Not included: the program folder of {name}. Use Uninstall… to remove the app itself — deleting its files would leave it broken.", false));

        if (plan.Targets.Count == 0)
        {
            if (notes.Count == 0) return;
            Dialog.Info(this, "Move to Recycle Bin", "This can't be moved to the Recycle Bin",
                string.Join(Environment.NewLine + Environment.NewLine, notes.Select(n => n.Item1)));
            return;
        }

        if (plan.AlsoRemoves.Count > 0)
            notes.Insert(0, ($"This also removes data of: {string.Join(", ", plan.AlsoRemoves)}.", true));

        var heading = plan.Targets.Count == 1
            ? $"Move “{PathUtil.Leaf(plan.Targets[0].Path)}” to the Recycle Bin?"
            : $"Move {plan.Targets.Count} items to the Recycle Bin?";
        var intro = $"Frees about {Format.Size(plan.TotalSize)} once you empty the Recycle Bin. " +
                    (plan.Targets.Count == 1 ? "This will be removed:" : "These will be removed:");
        var list = plan.Targets.Select(t => (t.Path, Format.Size(t.Size))).ToList();
        if (!Dialog.Confirm(this, "Move to Recycle Bin", heading, intro, list, notes, "Move to Recycle Bin", danger: true))
            return;

        var failures = new List<(string, string)>();
        var hwnd = new WindowInteropHelper(this).Handle;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            foreach (var t in plan.Targets)
            {
                if (Shell.MoveToRecycleBin(hwnd, t.Path, out var error)) Deletion.ApplyRemoved(data, t);
                else failures.Add((t.Path, error ?? "failed"));
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        Tree.SelectedItems.Clear();
        Rebuild();
        if (failures.Count > 0)
            Dialog.Info(this, "Move to Recycle Bin", "Some items couldn't be moved to the Recycle Bin",
                "They may be in use, or need administrator rights. Partly removed folders show their old size until you rescan.", failures);
    }
}
