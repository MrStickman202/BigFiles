namespace BigFiles.Core;

public enum RowType { Group, Item, Smaller, SmallerItem, Remainder }

public enum SortColumn { Name, Size, Kind, Location }

/// <summary>One line of the tree table (UI-independent).</summary>
public sealed class RowData
{
    public required RowType Type { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public string KindText { get; init; } = "";
    public string Location { get; init; } = "";
    public string? Tooltip { get; init; }
    public required Group Group { get; init; }
    public Item? Item { get; init; }
    public int Level { get; init; }
    public bool Expandable { get; init; }

    public override string ToString() => $"{new string(' ', Level * 2)}{Name}  {Format.Size(Size)}  {KindText}  {Location}";
}

public sealed class ViewOptions
{
    public long MinSize { get; init; } = 50 * Format.MB;
    public string Search { get; init; } = "";
    public SortColumn Sort { get; init; } = SortColumn.Size;
    public bool Descending { get; init; } = true;
}

/// <summary>Builds the visible rows: groups ≥ the filter, their big items, and the "Smaller files" breakdown.</summary>
public static class TreeBuilder
{
    public const int MaxSmallerItems = 300;

    public static List<RowData> Groups(ScanData data, ViewOptions o)
    {
        var q = o.Search.Trim();
        var rows = new List<RowData>();
        foreach (var g in data.Table.Groups)
        {
            if (g.Size <= 0 || g.Size < o.MinSize) continue;
            if (q.Length > 0 && !GroupMatches(g, data.Known, q) && !g.Items.Any(i => i.Size >= o.MinSize && ItemMatches(i, data.Known, q)))
                continue;
            rows.Add(GroupRow(g, data.Known));
        }
        return Sort(rows, o);
    }

    public static RowData GroupRow(Group g, KnownPaths known)
    {
        var roots = g.VisibleRoots.ToList();
        string location = roots.Count switch
        {
            0 => "",
            1 => known.Pretty(roots[0].Path),
            _ => $"{roots.Count} locations",
        };
        var tip = roots.Count > 1 ? string.Join("\n", roots.OrderByDescending(r => r.Size).Select(r => $"{known.Pretty(r.Path)}  ({Format.Size(r.Size)})")) : null;
        return new RowData
        {
            Type = RowType.Group,
            Key = "g:" + g.Key,
            Name = g.Name,
            Size = g.Size,
            KindText = g.KindText,
            Location = location,
            Tooltip = tip,
            Group = g,
            Level = 0,
            Expandable = true,
        };
    }

    public static bool GroupMatches(Group g, KnownPaths known, string q) =>
        g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
        || g.VisibleRoots.Any(r => r.Path.Contains(q, StringComparison.OrdinalIgnoreCase) || known.Pretty(r.Path).Contains(q, StringComparison.OrdinalIgnoreCase));

    public static bool ItemMatches(Item i, KnownPaths known, string q) =>
        i.Path.Contains(q, StringComparison.OrdinalIgnoreCase) || known.Pretty(i.Path).Contains(q, StringComparison.OrdinalIgnoreCase);

    /// <summary>Children of a group row or of a "Smaller files" row.</summary>
    public static List<RowData> Children(RowData parent, ScanData data, ViewOptions o)
    {
        var g = parent.Group;
        var known = data.Known;
        var q = o.Search.Trim();
        bool filtered = q.Length > 0 && !GroupMatches(g, known, q);

        if (parent.Type == RowType.Group)
        {
            var listed = g.Items.Where(i => i.Size >= o.MinSize && (!filtered || ItemMatches(i, known, q))).ToList();
            var rows = Sort(listed.Select(i => ItemRow(i, known, RowType.Item, 1)).ToList(), o);
            if (filtered) return rows;

            long rest = g.Size - listed.Sum(i => i.Size);
            if (rest > 0)
            {
                bool hasSmallerItems = g.Items.Any(i => i.Size < o.MinSize);
                rows.Add(hasSmallerItems
                    ? new RowData
                    {
                        Type = RowType.Smaller, Key = "s:" + g.Key, Name = "Smaller files", Size = rest,
                        Location = $"under {Format.Size(o.MinSize)} each", Group = g, Level = 1, Expandable = true,
                    }
                    : TinyRow(g, rest, g.Files - listed.Count, 1));
            }
            return rows;
        }

        if (parent.Type == RowType.Smaller)
        {
            var listedCount = g.Items.Count(i => i.Size >= o.MinSize);
            var under = g.Items.Where(i => i.Size < o.MinSize).OrderByDescending(i => i.Size).ToList();
            var top = under.Take(MaxSmallerItems).ToList();
            var rows = Sort(top.Select(i => ItemRow(i, known, RowType.SmallerItem, 2)).ToList(), o);
            long remainder = parent.Size - top.Sum(i => i.Size);
            long remainderFiles = g.Files - listedCount - top.Count;
            if (remainder > 0 || remainderFiles > 0)
            {
                if (under.Count > MaxSmallerItems)
                    rows.Add(new RowData
                    {
                        Type = RowType.Remainder, Key = "r:" + g.Key, Name = $"{Format.Count(remainderFiles)} more files",
                        Size = remainder, Location = $"each under {Format.Size(top[^1].Size)}", Group = g, Level = 2,
                    });
                else
                    rows.Add(TinyRow(g, remainder, remainderFiles, 2));
            }
            return rows;
        }

        return new List<RowData>();
    }

    static RowData TinyRow(Group g, long size, long files, int level) => new()
    {
        Type = RowType.Remainder, Key = "r:" + g.Key, Name = "Tiny files (under 1 MB each)", Size = size,
        Location = Format.Files(Math.Max(0, files)), Group = g, Level = level,
    };

    static RowData ItemRow(Item i, KnownPaths known, RowType type, int level) => new()
    {
        Type = type,
        Key = "i:" + i.Path,
        Name = i.Name,
        Size = i.Size,
        KindText = i.KindText,
        Location = known.Pretty(PathUtil.Parent(i.Path) ?? i.Path),
        Tooltip = i.Path,
        Group = i.Group,
        Item = i,
        Level = level,
    };

    public static List<RowData> Sort(List<RowData> rows, ViewOptions o)
    {
        Comparison<RowData> cmp = o.Sort switch
        {
            SortColumn.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            SortColumn.Kind => (a, b) => string.Compare(a.KindText, b.KindText, StringComparison.CurrentCultureIgnoreCase),
            SortColumn.Location => (a, b) => string.Compare(a.Location, b.Location, StringComparison.CurrentCultureIgnoreCase),
            _ => (a, b) => a.Size.CompareTo(b.Size),
        };
        rows.Sort((a, b) =>
        {
            int c = cmp(a, b);
            if (o.Descending) c = -c;
            if (c == 0) c = b.Size.CompareTo(a.Size);
            if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            return c;
        });
        return rows;
    }
}
