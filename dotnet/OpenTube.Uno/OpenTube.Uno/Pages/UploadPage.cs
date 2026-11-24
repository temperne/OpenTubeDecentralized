using Microsoft.UI.Xaml.Automation;
using OpenTube.Client;
using OpenTube.Core.BitTorrent;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>
/// Adding a video: pick it (copied into the app, with progress), its torrent and a still are made, then the details
/// (channel, title, description, tags, comments) and where it's kept (one of your computers, or this device). Publishing
/// goes step by step: added to the channel, the posting fee paid, then sent to your computer with a progress bar,
/// sizes, speed and time left, until the computer has all of it.
/// </summary>
public sealed partial class UploadPage : Page
{
    readonly StackPanel col; string? chainArg;
    Files.Picked? picked; TorrentMeta? meta; string thumb = "";
    public UploadPage()
    {
        Background = Application.Current.Resources["BackgroundBrush"] as Brush;
        var (scroll, c) = Ui.Page(); col = c; Content = Ui.WithBack("Add a video", scroll);
        Loaded += (_, _) => Build();
    }
    protected override void OnNavigatedTo(NavigationEventArgs e) => chainArg = e.Parameter as string is { Length: 64 } s ? s : null;

    void Build()
    {
        var S = Svc.S; col.Children.Clear();
        if (S == null || !S.Ready) { col.Children.Add(Sub("Starting…")); _ = Task.Delay(1500).ContinueWith(_ => DispatcherQueue.TryEnqueue(Build)); return; }
        var chans = S.Publish.MyChannels();
        if (chans.Count == 0) { col.Children.Add(Card(H("Create your channel first"), Text("To add a video, you need a channel. It takes a few seconds."), Btn("Create a channel", () => App.Open(typeof(YouPage), "channels"), primary: true))); return; }
        var chain = Pick(chans.Select(c => (c.Id, c.Name)), chainArg ?? chans[0].Id);
        var title = Input("Title", max: 140); AutomationProperties.SetAutomationId(title, "upload-title");
        var desc = new TextBox { PlaceholderText = "Description", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxLength = 5000 };
        var tags = Input("Tags, e.g. cooking, pasta, italian (up to 8)", max: 200);
        var policy = Pick([("", "Comments: channel default"), ("anyone", "Comments: anyone"), ("approved", "Comments: approved users only"), ("off", "Comments: off")]);
        var nodes = S.Publish.StorageNodes();
        if (nodes.Count == 0 && Environment.GetEnvironmentVariable("OPENTUBE_WAIT_COMPUTER") == "1") { col.Children.Add(Sub("Waiting for your computer…")); _ = Task.Delay(1500).ContinueWith(_ => DispatcherQueue.TryEnqueue(Build)); return; }
        var keep = Pick(nodes.Select(n => (n.Node, "Kept on " + n.Name)).Append(("", "This device only (keep the app open while people watch)")));
        var refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        refresh.Tick += (_, _) =>
        {   // computers that came online meanwhile join the choices
            var now = S.Publish.StorageNodes(); if (now.Count == nodes.Count) return; var cur = keep.Value(); nodes = now;
            keep.Items.Clear(); foreach (var (v, l) in nodes.Select(n => (n.Node, "Kept on " + n.Name)).Append(("", "This device only (keep the app open while people watch)"))) keep.Items.Add(new ComboBoxItem { Content = l, Tag = v });
            keep.SelectedIndex = Math.Max(0, keep.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == cur && cur.Length > 0));
        };
        refresh.Start(); Unloaded += (_, _) => refresh.Stop();
        var fileInfo = Sub("No video chosen yet."); var bar = new ProgressBar { Maximum = 1, Visibility = Visibility.Collapsed };
        var preview = new Image { MaxHeight = 200, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        var pickStatus = Status();
        var steps = new StackPanel { Spacing = 8 }; var status = Status();
        Button? publish = null;
        var choose = Btn("Choose a video…", () => _ = Choose(), primary: true, id: "upload-choose");
        async Task Choose()
        {
            picked = null; meta = null; thumb = ""; preview.Visibility = Visibility.Collapsed; publish!.IsEnabled = false;
            var t0 = DateTime.UtcNow;
            try
            {
                pickStatus.Say("Reading the file…"); bar.Visibility = Visibility.Visible; bar.Value = 0;
                picked = await Files.OpenVideo((done, total) => DispatcherQueue.TryEnqueue(() => { bar.Value = total > 0 ? (double)done / total : 0; pickStatus.Say($"Reading the file… {Svc.Bytes(done)} of {Svc.Bytes(total)}"); }));
                if (picked == null) { pickStatus.Say(null); bar.Visibility = Visibility.Collapsed; return; }
                if (title.Text.Length == 0) title.Text = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(picked.Name), "[_-]+", " ");
                pickStatus.Say($"Making its torrent (every piece is fingerprinted, {Svc.Bytes(picked.Size)})…"); bar.IsIndeterminate = true;
                meta = await Svc.Video.Share(picked.Path, picked.Name);
                pickStatus.Say("Taking a still for its picture…");
                thumb = await Svc.Video.Thumbnail(picked.Path, meta.InfoHash, picked.Name) ?? "";
                if (thumb.Length > 0 && await Svc.Thumb(thumb) is { } img) { preview.Source = img; preview.Visibility = Visibility.Visible; }
                bar.IsIndeterminate = false; bar.Visibility = Visibility.Collapsed;
                fileInfo.Text = $"{picked.Name} · {Svc.Bytes(picked.Size)} · posting costs {S.Publish.PostCost(picked.Size) / 100:0.##} coins (you have {S.Balance(S.Me.pub) / 100:0.##})";
                pickStatus.Say(null); publish.IsEnabled = true;
            }
            catch (Exception e) { bar.Visibility = Visibility.Collapsed; pickStatus.Say("Couldn’t use that video: " + e.Message); Svc.Report(e); }
        }
        publish = Btn("Publish to channel", () => _ = Publish(), primary: true, id: "upload-publish"); publish.IsEnabled = false;
        async Task Publish()
        {
            if (picked == null || meta == null) return;
            if (string.IsNullOrWhiteSpace(title.Text)) { status.Say("Add a title"); return; }
            publish.IsEnabled = false; choose.IsEnabled = false; steps.Children.Clear();
            var add = Step("Adding it to your channel"); var pay = Step("Paying the posting fee");
            var keepOn = keep.Value().Length > 0 ? keep.Value() : null;
            var send = keepOn != null ? Step("Sending it to " + (nodes.FirstOrDefault(n => n.Node == keepOn).Name ?? "your computer")) : null;
            try
            {
                add.Now();
                var b = await S.Publish.Video(chain.Value(), meta, picked.Name, picked.Size, picked.Type, title.Text, desc.Text, thumb, m => DispatcherQueue.TryEnqueue(() => { if (m.StartsWith("Paying")) { add.Done(); pay.Now(); } }), tags.Text, policy.Value());
                add.Done(); pay.Done();
                if (keepOn != null && send != null)
                {
                    send.Now(); DateTime t0 = DateTime.UtcNow; long r0 = -1;
                    await S.Publish.Store(keepOn, chain.Value(), meta.InfoHash, _ => { }, p => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (r0 < 0 && p.Received > 0) { r0 = p.Received; t0 = DateTime.UtcNow; }
                        var secs = (DateTime.UtcNow - t0).TotalSeconds; var bps = r0 >= 0 && secs > 2 ? (p.Received - r0) / secs : 0;
                        var left = bps > 0 ? (p.Size - p.Received) / bps : -1;
                        send.Progress(p.Fraction, $"{Math.Round(p.Fraction * 100)}% · {Svc.Bytes(p.Received)} of {Svc.Bytes(p.Size > 0 ? p.Size : picked.Size)}" + (bps > 0 ? $" · {Svc.Bytes(bps)}/s · about {(left < 60 ? $"{Math.Ceiling(left)} s" : $"{Math.Ceiling(left / 60)} min")} left" : ""),
                            (p.Peers > 0 ? $"Connected to your computer ({p.Peers} connection{(p.Peers == 1 ? "" : "s")})" : "Not connected to your computer yet") + $" · this device has sent {Svc.Bytes(Svc.Video.Uploaded(meta.InfoHash))}" + (p.Note != null ? "\n" + p.Note : "") + "\nKeep this open until it reaches 100%: after that it stays online without this device.");
                    }));
                    send.Done();
                }
                status.Say(keepOn != null ? "Published, and your computer has all of it: it stays online without this device." : "Published. This device shares it while the app is open.");
                Console.WriteLine("[upload] published " + (string?)b["hash"]);
                await Task.Delay(1200); App.Back(); App.Open(typeof(WatchPage), new WatchArgs(chain.Value(), (string)b["hash"]!));
            }
            catch (Exception e) { status.Say("Couldn’t publish: " + e.Message); Svc.Report(e); publish.IsEnabled = true; choose.IsEnabled = true; }
        }
        StepRow Step(string name) { var s = new StepRow(name); steps.Children.Add(s.View); return s; }
        col.Children.Add(Card(H("Add a video"), Sub("Channel"), chain, Row(choose), fileInfo, bar, pickStatus, preview, title, desc, tags, policy,
            keep, Sub("Run OpenTuber on a computer (Devices) to keep your videos online when this device isn’t."),
            publish, steps, status));
        if (Environment.GetEnvironmentVariable("OPENTUBE_OPEN_VIDEO") != null) _ = Choose().ContinueWith(_ => DispatcherQueue.TryEnqueue(() => { if (Environment.GetEnvironmentVariable("OPENTUBE_AUTOPUBLISH") == "1") _ = Publish(); }));   // (tests)
    }

    /// <summary>One step of publishing: waiting, happening (with a bar and details), done.</summary>
    sealed class StepRow
    {
        public readonly StackPanel View = new() { Spacing = 4 };
        readonly TextBlock dot = new() { Text = "○", Width = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold }, label = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        readonly ProgressBar bar = new() { Maximum = 1, Visibility = Visibility.Collapsed }; readonly TextBlock line = Sub(""), detail = Sub("");
        public StepRow(string name)
        {
            label.Text = name; var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; head.Children.Add(dot); head.Children.Add(label);
            View.Children.Add(head); View.Children.Add(bar); View.Children.Add(line); View.Children.Add(detail); View.Opacity = 0.5;
        }
        public void Now() { dot.Text = "●"; dot.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange); View.Opacity = 1; Console.WriteLine("[upload] " + label.Text); }
        public void Progress(double f, string l, string d) { bar.Visibility = Visibility.Visible; bar.Value = f; line.Text = l; detail.Text = d; Console.WriteLine($"[upload] {label.Text}: {l}"); }
        public void Done() { dot.Text = "✓"; dot.Foreground = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen); bar.Visibility = Visibility.Collapsed; line.Text = detail.Text = ""; View.Opacity = 1; }
    }
}
