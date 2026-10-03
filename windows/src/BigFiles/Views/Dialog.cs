using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BigFiles.Win;

namespace BigFiles.Views;

/// <summary>A small themed message / confirmation window (MessageBox can't follow dark mode).</summary>
internal sealed class Dialog : Window
{
    bool confirmed;

    Dialog(Window owner, string title)
    {
        Owner = owner;
        Title = title;
        Width = 580;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "WindowBg");
        SetResourceReference(ForegroundProperty, "Fg");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13;
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
    }

    /// <summary>Asks before doing something. <paramref name="list"/> rows are (path, size text).</summary>
    public static bool Confirm(Window owner, string title, string heading, string? intro,
                               IReadOnlyList<(string Text, string Detail)>? list, IEnumerable<(string Text, bool Warning)> notes,
                               string okText, bool danger)
    {
        var d = new Dialog(owner, title);
        d.Build(heading, intro, list, notes, okText, danger, cancelText: "Cancel");
        d.ShowDialog();
        return d.confirmed;
    }

    public static void Info(Window owner, string title, string heading, string? body,
                            IReadOnlyList<(string Text, string Detail)>? list = null)
    {
        var d = new Dialog(owner, title);
        d.Build(heading, body, list, Array.Empty<(string, bool)>(), "OK", false, cancelText: null);
        d.ShowDialog();
    }

    void Build(string heading, string? intro, IReadOnlyList<(string Text, string Detail)>? list,
               IEnumerable<(string Text, bool Warning)> notes, string okText, bool danger, string? cancelText)
    {
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock
        {
            Text = heading, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        if (!string.IsNullOrEmpty(intro))
            root.Children.Add(Muted(intro, new Thickness(0, 0, 0, 10)));

        if (list is { Count: > 0 })
        {
            var rows = new StackPanel();
            foreach (var (text, detail) in list)
            {
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text };
                var s = new TextBlock { Text = detail, Margin = new Thickness(16, 0, 0, 0) };
                s.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                Grid.SetColumn(s, 1);
                g.Children.Add(t);
                g.Children.Add(s);
                rows.Children.Add(g);
            }
            var box = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new ScrollViewer { Content = rows, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            };
            box.SetResourceReference(Border.BorderBrushProperty, "Border");
            box.SetResourceReference(Border.BackgroundProperty, "PanelBg");
            root.Children.Add(box);
        }

        foreach (var (text, warning) in notes)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            if (warning)
            {
                tb.FontWeight = FontWeights.SemiBold;
                tb.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            }
            root.Children.Add(tb);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = okText, MinWidth = 96 };
        ok.SetResourceReference(StyleProperty, danger ? "DangerButton" : "AccentButton");
        ok.Click += (_, _) => { confirmed = true; Close(); };
        if (cancelText != null)
        {
            var cancel = new Button { Content = cancelText, MinWidth = 96, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            Loaded += (_, _) => cancel.Focus(); // destructive actions need a deliberate click
        }
        else
        {
            ok.IsDefault = true;
            ok.IsCancel = true;
            buttons.Children.Add(ok);
            Loaded += (_, _) => ok.Focus();
        }
        root.Children.Add(buttons);
        Content = root;
    }

    static TextBlock Muted(string text, Thickness margin)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        return tb;
    }
}
