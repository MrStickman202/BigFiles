using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using BigFiles.Core;
using BigFiles.Win;

namespace BigFiles.Views;

/// <summary>One visible line of the tree table.</summary>
public sealed class RowVM : INotifyPropertyChanged
{
    readonly KnownPaths known;
    bool expanded;
    ImageSource? icon;
    bool iconLoaded;

    public RowVM(RowData data, KnownPaths known, bool expanded)
    {
        Data = data;
        this.known = known;
        this.expanded = expanded;
    }

    public RowData Data { get; }
    public string Key => Data.Key;
    public int Level => Data.Level;
    public string Name => Data.Name;
    public string SizeText => Format.Size(Data.Size);
    public string KindText => Data.KindText;
    public string Location => Data.Location;
    public string? Tooltip => Data.Type == RowType.Group ? null : Data.Tooltip;
    public string? LocationTooltip => Data.Tooltip ?? (Data.Location.Length > 0 ? Data.Location : null);
    public Thickness Indent => new(Data.Level * 20, 0, 0, 0);
    public Visibility ToggleVisibility => Data.Expandable ? Visibility.Visible : Visibility.Hidden;
    public FontWeight Weight => Data.Type == RowType.Group ? FontWeights.SemiBold : FontWeights.Normal;
    public bool IsMuted => Data.Type is RowType.Smaller or RowType.Remainder;

    public bool IsExpanded
    {
        get => expanded;
        set
        {
            if (expanded == value) return;
            expanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArrowAngle)));
        }
    }

    public double ArrowAngle => expanded ? 90 : 0;

    /// <summary>Loaded on first use, so only rows that are actually on screen pay for their icon.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (iconLoaded) return icon;
            iconLoaded = true;
            icon = LoadIcon();
            return icon;
        }
    }

    ImageSource? LoadIcon()
    {
        switch (Data.Type)
        {
            case RowType.Item:
            case RowType.SmallerItem:
                return Icons.ForExtension(Data.Name);
            case RowType.Group:
                var g = Data.Group;
                if (g.App != null) return Icons.ForApp(g.App);
                if (g.Key == "sys:recycle") return Icons.RecycleBin;
                var roots = g.VisibleRoots.ToList();
                if (g.Kind == GroupKind.Folder && roots.Count == 1 && known.UserFolders.Contains(roots[0].Path, PathUtil.Cmp))
                    return Icons.ForKnownFolder(roots[0].Path);
                return Icons.Folder;
            default:
                return null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
