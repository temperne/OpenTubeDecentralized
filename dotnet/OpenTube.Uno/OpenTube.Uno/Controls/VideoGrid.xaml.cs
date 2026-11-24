using OpenTube.Uno.Pages;

namespace OpenTube.Uno.Controls;

public sealed partial class VideoGrid : UserControl
{
    public VideoGrid() { this.InitializeComponent(); }
    public IList<VideoItem> Items { set => Repeater.ItemsSource = value; }
    void OnOpen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VideoItem v) return;
        if (v.Live && v.Stream != null) App.Open(typeof(LivePage), new LiveArgs(v.Chain, v.Stream));
        else App.Open(typeof(WatchPage), new WatchArgs(v.Chain, v.Hash));
    }
}
