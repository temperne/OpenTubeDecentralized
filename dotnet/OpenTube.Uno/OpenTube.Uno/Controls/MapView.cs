using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace OpenTube.Uno.Controls;

/// <summary>
/// A map: OpenStreetMap tiles drawn by the app (the same on phones, the web and computers). Drag to move, the wheel or
/// + / − to zoom, pins for places (tap one), and tapping the map itself picks a spot (adding a place).
/// </summary>
public sealed partial class MapView : Grid
{
    public sealed record Pin(double Lat, double Lon, string Icon, string Title, object? Tag = null);
    readonly Canvas tiles = new(), pins = new();
    double lat = 20, lon = 0; int zoom = 2;
    List<Pin> all = [];
    public event Action<Pin>? PinTapped;
    public event Action<double, double>? MapTapped;
    Windows.Foundation.Point? down; double downLat, downLon; bool moved;
    public MapView()
    {
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 170, 211, 223)); Height = 360; CornerRadius = new CornerRadius(12);
        tiles.IsHitTestVisible = false;
        Children.Add(tiles); Children.Add(pins);
        var zoomBar = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), Spacing = 4 };
        zoomBar.Children.Add(ZoomBtn("+", 1)); zoomBar.Children.Add(ZoomBtn("−", -1)); Children.Add(zoomBar);
        Children.Add(new TextBlock { Text = "© OpenStreetMap contributors", FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 2, 6, 2) });
        Clip = null; SizeChanged += (_, _) => { Clip = new RectangleGeometry { Rect = new(0, 0, ActualWidth, ActualHeight) }; Draw(); };
        PointerPressed += (_, e) => { down = e.GetCurrentPoint(this).Position; downLat = lat; downLon = lon; moved = false; CapturePointer(e.Pointer); };
        PointerMoved += (_, e) =>
        {
            if (down is not { } d0) return; var p = e.GetCurrentPoint(this).Position; var dx = p.X - d0.X; var dy = p.Y - d0.Y;
            if (!moved && Math.Abs(dx) + Math.Abs(dy) < 6) return; moved = true;
            var (x, y) = World(downLat, downLon); (lat, lon) = Geo(x - dx, y - dy); Draw();
        };
        PointerReleased += (_, e) =>
        {
            var p = e.GetCurrentPoint(this).Position; ReleasePointerCapture(e.Pointer);
            if (down != null && !moved) { var (cx, cy) = World(lat, lon); var (la, lo) = Geo(cx + p.X - ActualWidth / 2, cy + p.Y - ActualHeight / 2); MapTapped?.Invoke(la, lo); }
            down = null;
        };
        PointerWheelChanged += (_, e) => { Zoom(e.GetCurrentPoint(this).Properties.MouseWheelDelta > 0 ? 1 : -1); e.Handled = true; };
    }
    Button ZoomBtn(string t, int d) { var b = new Button { Content = t, Width = 40, Height = 40, Padding = new Thickness(0), FontSize = 20, Background = new SolidColorBrush(Microsoft.UI.Colors.White), Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black) }; b.Click += (_, _) => Zoom(d); return b; }
    void Zoom(int d) { zoom = Math.Clamp(zoom + d, 2, 18); Draw(); }
    /// <summary>The places to show; with fit, the map moves to show them all.</summary>
    public void SetPins(IEnumerable<Pin> list, bool fit)
    {
        all = list.ToList();
        if (fit && all.Count > 0)
        {
            double a = all.Min(p => p.Lat), b = all.Max(p => p.Lat), c = all.Min(p => p.Lon), e = all.Max(p => p.Lon);
            lat = (a + b) / 2; lon = (c + e) / 2;
            var w = Math.Max(ActualWidth, 300); var h = Math.Max(ActualHeight, 300); zoom = 16;
            while (zoom > 2) { var (x1, y1) = World(b, c); var (x2, y2) = World(a, e); if (x2 - x1 < w * 0.8 && y2 - y1 < h * 0.8) break; zoom--; }
        }
        Draw();
    }
    public void CenterOn(double la, double lo, int z) { lat = la; lon = lo; zoom = z; Draw(); }
    // Web Mercator: world pixels at this zoom (256 px tiles)
    (double X, double Y) World(double la, double lo)
    {
        var n = 256 * Math.Pow(2, zoom); var s = Math.Sin(Math.Clamp(la, -85, 85) * Math.PI / 180);
        return ((lo + 180) / 360 * n, (0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI)) * n);
    }
    (double Lat, double Lon) Geo(double x, double y)
    {
        var n = 256 * Math.Pow(2, zoom); var lo = x / n * 360 - 180; var m = Math.PI - 2 * Math.PI * y / n;
        return (180 / Math.PI * Math.Atan(Math.Sinh(m)), ((lo + 540) % 360) - 180);
    }
    readonly Dictionary<string, Image> cache = [];
    // (OpenStreetMap's tile servers want an app to say who it is; in a browser the browser does)
    static readonly HttpClient http = MakeHttp();
    static HttpClient MakeHttp() { var h = new HttpClient(); try { h.DefaultRequestHeaders.UserAgent.ParseAdd("OpenTuber/1.0 (+https://github.com/temperne/OpenTubeDecentralized)"); } catch { } return h; }
    static async Task Load(Image img, string key)
    {
        try
        {
            var bytes = await http.GetByteArrayAsync($"https://tile.openstreetmap.org/{key}.png");
            var bmp = new BitmapImage(); using var ms = new MemoryStream(bytes); await bmp.SetSourceAsync(ms.AsRandomAccessStream()); img.Source = bmp;
        }
        catch { }
    }
    void Draw()
    {
        if (ActualWidth <= 0) return;
        var (cx, cy) = World(lat, lon); double left = cx - ActualWidth / 2, top = cy - ActualHeight / 2; var max = 1 << zoom;
        tiles.Children.Clear();
        for (var tx = (int)Math.Floor(left / 256); tx * 256 < left + ActualWidth; tx++)
            for (var ty = (int)Math.Floor(top / 256); ty * 256 < top + ActualHeight; ty++)
            {
                if (ty < 0 || ty >= max) continue; var wx = ((tx % max) + max) % max; var key = $"{zoom}/{wx}/{ty}";
                if (!cache.TryGetValue(key, out var img))
                {
                    img = new Image { Width = 256, Height = 256 }; _ = Load(img, key);
                    if (cache.Count > 300) cache.Clear(); cache[key] = img;
                }
                Canvas.SetLeft(img, tx * 256 - left); Canvas.SetTop(img, ty * 256 - top); tiles.Children.Add(img);
            }
        pins.Children.Clear();
        foreach (var p in all)
        {
            var (x, y) = World(p.Lat, p.Lon); x -= left; y -= top;
            if (x < -20 || y < -40 || x > ActualWidth + 20 || y > ActualHeight + 20) continue;
            var b = new Button { Content = new TextBlock { Text = p.Icon, FontSize = 18 }, Padding = new Thickness(4, 2, 4, 2), MinWidth = 0, MinHeight = 0, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Microsoft.UI.Colors.White), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Red), BorderThickness = new Thickness(2) };
            ToolTipService.SetToolTip(b, p.Title);
            b.Click += (_, _) => PinTapped?.Invoke(p);
            Canvas.SetLeft(b, x - 16); Canvas.SetTop(b, y - 34); pins.Children.Add(b);
        }
    }
}
