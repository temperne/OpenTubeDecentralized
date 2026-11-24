using Microsoft.UI.Xaml.Automation;

namespace OpenTube.Uno.Controls;

/// <summary>
/// The app's building blocks, shared by every screen so they look and behave the same: cards, headings, notes, rows,
/// buttons, inputs and a status line. All plain Uno (WinUI) controls with the app's Material theme.
/// </summary>
public static class Ui
{
    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    public static Brush Surface => Res("SurfaceBrush");
    public static Brush Muted => Res("OnSurfaceVariantBrush");
    public static Brush Primary => Res("PrimaryBrush");

    /// <summary>A rounded panel holding a section.</summary>
    public static Border Card(params UIElement[] children)
    {
        var sp = new StackPanel { Spacing = 10 }; foreach (var c in children) if (c != null) sp.Children.Add(c);
        return new Border { Background = Surface, CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Child = sp };
    }
    public static TextBlock H(string text, double size = 18) => new() { Text = text, FontSize = size, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
    public static TextBlock Sub(string text) => new() { Text = text, FontSize = 13, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    public static TextBlock Text(string text, double size = 15) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    public static TextBlock Mono(string text) => new() { Text = text, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = Muted };
    /// <summary>Items side by side, wrapping onto the next line when they don't fit.</summary>
    public static Panel Row(params UIElement[] items)
    {
        var p = new WrapRow(); foreach (var i in items) if (i != null) p.Children.Add(i); return p;
    }
    public static Button Btn(string text, Action onClick, bool primary = false, string? id = null)
    {
        var b = new Button { Content = text };
        b.Style = (Style)Application.Current.Resources[primary ? "FilledButtonStyle" : "FilledTonalButtonStyle"];
        if (id != null) AutomationProperties.SetAutomationId(b, id);
        b.Click += (_, _) => onClick(); return b;
    }
    public static TextBox Input(string placeholder, string text = "", int max = 200)
        => new() { PlaceholderText = placeholder, Text = text, MaxLength = max, HorizontalAlignment = HorizontalAlignment.Stretch };
    public static PasswordBox Password(string placeholder) => new() { PlaceholderText = placeholder, HorizontalAlignment = HorizontalAlignment.Stretch };
    public static ComboBox Pick(IEnumerable<(string Value, string Label)> items, string? selected = null)
    {
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (v, l) in items) cb.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        cb.SelectedIndex = Math.Max(0, cb.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == selected));
        return cb;
    }
    public static string Value(this ComboBox cb) => (cb.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    /// <summary>A line that says what's happening (or what went wrong).</summary>
    public static TextBlock Status() => new() { FontSize = 13, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    public static void Say(this TextBlock status, string? text) { status.Text = text ?? ""; status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible; }
    /// <summary>Run an action, saying what's happening, then "Done." or what went wrong.</summary>
    public static async Task Do(this TextBlock status, string doing, Func<Task> act, string done = "Done.")
    {
        status.Say(doing);
        try { await act(); status.Say(done); } catch (Exception e) { status.Say(e.Message); OpenTube.Uno.Services.Svc.Report(e); }
    }
    /// <summary>A page's scrolling column (centered, readable width on wide screens).</summary>
    public static (ScrollViewer Scroll, StackPanel Column) Page(double maxWidth = 900)
    {
        var col = new StackPanel { Spacing = 14, Padding = new Thickness(16), MaxWidth = maxWidth };
        return (new ScrollViewer { Content = col }, col);
    }
    /// <summary>Segmented choice (tabs inside a page): the chosen one filled, the others outlined.</summary>
    public static (Panel Bar, Func<string> Current) Segments(IEnumerable<(string Key, string Label)> items, string first, Action<string> changed)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; var current = first;
        void Paint() { foreach (var x in bar.Children.OfType<Button>()) x.Style = (Style)Application.Current.Resources[(string)x.Tag == current ? "FilledButtonStyle" : "OutlinedButtonStyle"]; }
        foreach (var (k, l) in items)
        {
            var b = new Button { Content = l, Tag = k };
            AutomationProperties.SetAutomationId(b, "seg-" + k);
            b.Click += (s, _) => { current = (string)((FrameworkElement)s).Tag; Paint(); changed(current); };
            bar.Children.Add(b);
        }
        Paint();
        var scroll = new ScrollViewer { Content = bar, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled };
        var holder = new Grid(); holder.Children.Add(scroll);
        return (holder, () => current);
    }
    /// <summary>A page opened over the app frame: a top bar with back and a title, and the page under it.</summary>
    public static Grid WithBack(string title, UIElement content)
    {
        var g = new Grid { Background = (Brush)Application.Current.Resources["BackgroundBrush"] };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var back = new Button { Content = new SymbolIcon(Symbol.Back), Style = (Style)Application.Current.Resources["IconButtonStyle"] };
        AutomationProperties.SetName(back, "Back"); back.Click += (_, _) => App.Back();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(4, 4, 16, 4), Background = Surface };
        top.Children.Add(back); top.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(top); Grid.SetRow((FrameworkElement)content, 1); g.Children.Add(content);
        return g;
    }
}

/// <summary>Lays children out left to right, wrapping to a new line when the width runs out.</summary>
public sealed class WrapRow : Panel
{
    public double Gap { get; set; } = 8;
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size avail)
    {
        double x = 0, y = 0, line = 0, w = 0;
        foreach (var c in Children)
        {
            c.Measure(new Windows.Foundation.Size(avail.Width, double.PositiveInfinity)); var d = c.DesiredSize;
            if (x > 0 && x + d.Width > avail.Width) { y += line + Gap; x = 0; line = 0; }
            x += d.Width + Gap; line = Math.Max(line, d.Height); w = Math.Max(w, x);
        }
        return new(double.IsInfinity(avail.Width) ? w : avail.Width, y + line);
    }
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size size)
    {
        double x = 0, y = 0, line = 0;
        foreach (var c in Children)
        {
            var d = c.DesiredSize;
            if (x > 0 && x + d.Width > size.Width) { y += line + Gap; x = 0; line = 0; }
            c.Arrange(new Windows.Foundation.Rect(x, y, d.Width, d.Height)); x += d.Width + Gap; line = Math.Max(line, d.Height);
        }
        return size;
    }
}
