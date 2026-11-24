namespace OpenTube.Uno.Pages;

/// <summary>A screen not converted yet (the proof covers the frame, Home, Explore and watching).</summary>
public sealed partial class ComingPage : Page
{
    public ComingPage() { this.InitializeComponent(); }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Title.Text = (e.Parameter as string) switch { "posts" => "Posts", "messages" => "Messages", "you" => "You", "upload" => "Add a video", "golive" => "Go live", "notifications" => "Notifications", "live" => "Live stream", _ => "Coming soon" };
    }
}
