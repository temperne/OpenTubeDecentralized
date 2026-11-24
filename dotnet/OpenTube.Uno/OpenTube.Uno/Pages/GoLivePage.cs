using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>
/// Going live: from this device's camera (in the browser now; the phone's camera comes next) — choose the camera and
/// microphone or your screen, see the preview, go live, end it and publish or discard the recording — or with OBS
/// through one of your computers (its server and stream key, YouTube forwarding, the channel it goes live on).
/// </summary>
public sealed partial class GoLivePage : Page
{
    readonly StackPanel col; string? chainArg;
    LiveBroadcast? live; LiveBroadcast? ended;
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    public GoLivePage()
    {
        Background = Application.Current.Resources["BackgroundBrush"] as Brush;
        var (scroll, c) = Ui.Page(); col = c; Content = Ui.WithBack("Go live", scroll);
        Loaded += (_, _) => { Build(); tick.Start(); };
        Unloaded += async (_, _) =>
        {
            tick.Stop(); if (live != null) await End();
#if __WASM__
            else { CameraBridge.OnFrame = null; await CameraBridge.Stop(); }   // (leaving the page lets go of the camera)
#endif
        };
    }
    protected override void OnNavigatedTo(NavigationEventArgs e) => chainArg = e.Parameter as string is { Length: 64 } s ? s : null;

    ComboBox? chain; TextBox? title; TextBlock? camStatus; Border? preview; Button? goBtn, endBtn;
    void Build()
    {
        var S = Svc.S; col.Children.Clear();
        if (S == null || !S.Ready) { col.Children.Add(Sub("Starting…")); _ = Task.Delay(1500).ContinueWith(_ => DispatcherQueue.TryEnqueue(Build)); return; }
        var chans = S.Publish.MyChannels();
        var auto = Svc.StartOption == "golive-auto";
        if (chans.Count == 0)
        {   // (a new channel takes a little while to register: keep looking)
            col.Children.Add(Card(H("Create your channel first"), Text("To go live, you need a channel. It takes a few seconds."), Btn("Create a channel", () => App.Open(typeof(YouPage), "channels"), primary: true)));
            _ = Task.Delay(3000).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) Build(); })); return;
        }
        chain = Pick(chans.Select(c => (c.Id, c.Name)), chainArg ?? chans[0].Id); title = Input("Title", "Live", 140);
        col.Children.Add(Card(H("Your stream"), Sub("Channel"), chain, title));
        Camera();
        Obs();
#if __WASM__
        if (auto && live == null) _ = Task.Delay(3000).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => _ = Start()));   // (tests)
#endif
    }

    // ---- this device's camera
    void Camera()
    {
        camStatus = Status();
#if __WASM__
        var cam = Pick([("", "Default camera")]); var mic = Pick([("", "Default microphone"), ("none", "No microphone")]);
        var shown = new Image { Stretch = Stretch.Uniform };
        preview = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Height = 240, CornerRadius = new CornerRadius(8), Child = shown };
        Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? bmp = null;
        CameraBridge.OnFrame = (px, w, h) =>
        {   // (the preview: frames from the camera, drawn by the app)
            if (bmp == null || bmp.PixelWidth != w || bmp.PixelHeight != h) { bmp = new(w, h); shown.Source = bmp; }
            using (var st = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bmp.PixelBuffer)) st.Write(px, 0, Math.Min(px.Length, w * h * 4));
            bmp.Invalidate();
        };
        async Task LoadDevices()
        {
            var text = await CameraBridge.Devices(); var parts = text.Split("\n\n");
            Fill(cam, [("", "Default camera"), .. parts[0].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (l.Split('\t')[0], l.Split('\t').ElementAtOrDefault(1) ?? "Camera")), ("screen", "Share my screen or a window")]);
            Fill(mic, [("", "Default microphone"), .. (parts.ElementAtOrDefault(1) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (l.Split('\t')[0], l.Split('\t').ElementAtOrDefault(1) ?? "Microphone")), ("none", "No microphone")]);
        }
        async Task Open() { camStatus!.Say("Opening…"); var err = await CameraBridge.Preview(cam.Value(), mic.Value()); camStatus.Say(err.Length > 0 ? "Couldn’t open that source: " + err : null); await LoadDevices(); }
        cam.SelectionChanged += (_, _) => { if (live == null && !starting && !filling) _ = Open(); }; mic.SelectionChanged += (_, _) => { if (live == null && !starting && !filling) _ = Open(); };
        _ = LoadDevices();
        goBtn = Btn("● Go live", () => _ = Start(), primary: true, id: "golive-start"); endBtn = Btn("■ End stream", () => _ = End(), primary: true); endBtn.Visibility = Visibility.Collapsed;
        col.Children.Add(Card(H("With this device’s camera"), Row(cam, mic), Btn("Allow camera & microphone", () => _ = Open()), preview, Row(goBtn, endBtn), camStatus,
            Sub("Your computers relay it to viewers, and one records it so you can publish it afterwards.")));
#else
        col.Children.Add(Card(H("With this device’s camera"), Text("Going live from the phone’s camera is the next step of the app. Meanwhile use OBS through one of your computers (below), or go live from the website in a browser.")));
#endif
        tick.Tick += (_, _) =>
        {
            if (live != null) camStatus!.Say($"{(live.GroupStatus != null ? live.GroupStatus + " · " : "")}{live.Status} · {Svc.Bytes(live.Bytes)} sent · {live.Viewers} watching");
            if (ended != null) Ended();
        };
    }
    bool filling, starting;
    /// <summary>New device lists (names appear once allowed), keeping the choice, without counting as choosing a source.</summary>
    void Fill(ComboBox cb, IEnumerable<(string V, string L)> items)
    {
        filling = true;
        try
        {
            var list = items.ToList(); var cur = cb.Value();
            var same = cb.Items.Count == list.Count && cb.Items.Cast<ComboBoxItem>().Select(i => ((string)i.Tag, (string)i.Content)).SequenceEqual(list.Select(x => (x.V, x.L)));
            if (same) return;
            cb.IsDropDownOpen = false; cb.Items.Clear();
            foreach (var (v, l) in list) cb.Items.Add(new ComboBoxItem { Content = l, Tag = v });
            cb.SelectedIndex = Math.Max(0, cb.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == cur));
        }
        finally { filling = false; }
    }
#if __WASM__
    async Task Start()
    {
        var S = Svc.S; if (starting || live != null) return; starting = true; goBtn!.IsEnabled = false; camStatus!.Say("Starting the camera…");
        var mime = await CameraBridge.Start();
        if (mime.StartsWith("error:")) { camStatus.Say(mime[6..]); goBtn.IsEnabled = true; starting = false; return; }
        try
        {
            live = await LiveBroadcast.Start(S, chain!.Value(), title!.Text, mime);
            CameraBridge.OnChunk = b => live?.Chunk(b);
            goBtn.Visibility = Visibility.Collapsed; endBtn!.Visibility = Visibility.Visible;
            Console.WriteLine("[golive] live " + live.Stream);
        }
        catch (Exception e) { camStatus.Say("Couldn’t go live: " + e.Message); await CameraBridge.Stop(); goBtn.IsEnabled = true; }
        starting = false;
    }
    async Task End()
    {
        await CameraBridge.Stop(); CameraBridge.OnChunk = null;
        if (live != null) { await live.Stop(); ended = live; live = null; }
        goBtn!.Visibility = Visibility.Visible; goBtn.IsEnabled = true; endBtn!.Visibility = Visibility.Collapsed; camStatus!.Say("The stream has ended."); Ended();
    }
#else
    Task Start() => Task.CompletedTask;
    Task End() => Task.CompletedTask;
#endif

    // ---- after a stream: its recording, to publish or discard (your computer's, or this device's own copy)
    StackPanel? endedPanel; string endedKey = "";
    void Ended()
    {
        var S = Svc.S; var e = ended!;
        var nrec = S.NodeRecordings(e.Chain).FirstOrDefault(r => r.Stream == e.Stream);
        var settled = S.Chain(e.Chain)?.LiveEnds.GetValueOrDefault(e.Stream) is "published" or "discarded";
        var key = $"{nrec != null}|{settled}|{e.LocalFile != null && File.Exists(e.LocalFile)}";
        if (key == endedKey) return; endedKey = key;
        if (endedPanel == null) { endedPanel = new StackPanel { Spacing = 8 }; col.Children.Insert(1, Card(H("Your stream has ended"), endedPanel)); }
        endedPanel.Children.Clear(); var st = Status();
        if (settled) { endedPanel.Children.Add(Text(S.Chain(e.Chain)!.LiveEnds[e.Stream] == "published" ? "The recording was published as a video." : "The recording was discarded.")); endedPanel.Children.Add(Btn("Open the channel", () => ChannelPage.Open(e.Chain))); return; }
        var rt = Input("Title", nrec?.Title ?? title!.Text, 140); var rd = Input("Description", max: 5000);
        if (nrec != null)
        {
            endedPanel.Children.Add(Sub($"Your computer recorded it ({(Js.Num(nrec.R["size"]) ?? 0) / 1e6:0.0} MB). Publish it as a video, or discard it."));
            endedPanel.Children.Add(rt); endedPanel.Children.Add(rd);
            endedPanel.Children.Add(Row(Btn("Publish recording", () => _ = st.Do("Publishing…", async () => { await S.SettleRecording(e.Chain, nrec, true, rt.Text, rd.Text); endedKey = ""; }, "Recording published as a video."), primary: true), Btn("Discard", () => _ = st.Do("Discarding…", async () => { await S.SettleRecording(e.Chain, nrec, false, "", ""); endedKey = ""; }, "Recording discarded."))));
        }
        else if (e.LocalFile != null && File.Exists(e.LocalFile))
        {
            endedPanel.Children.Add(Sub($"This device kept the recording ({Svc.Bytes(new FileInfo(e.LocalFile).Length)}). Publish it as a video, or discard it."));
            endedPanel.Children.Add(rt); endedPanel.Children.Add(rd);
            endedPanel.Children.Add(Row(Btn("Publish recording", () => _ = st.Do("Sharing the recording…", async () =>
            {
                var name = string.Concat((rt.Text.Trim().Length > 0 ? rt.Text.Trim() : "Live").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".webm";
                var path = Path.Combine(Path.GetDirectoryName(e.LocalFile)!, name); File.Move(e.LocalFile, path, true);
                var meta = await Svc.Video.Share(path, name);
                await S.Publish.Video(e.Chain, meta, name, new FileInfo(path).Length, "video/webm", rt.Text.Trim().Length > 0 ? rt.Text : "Live", rd.Text, "", _ => { }, stream: e.Stream);
                await Task.Run(() => S.Engine.Append(e.Chain, new JsonObject { ["type"] = "liveend", ["stream"] = e.Stream, ["outcome"] = "published" })); endedKey = "";
            }, "Recording published as a video."), primary: true),
                Btn("Discard", () => _ = st.Do("Discarding…", async () => { try { File.Delete(e.LocalFile); } catch { } await Task.Run(() => S.Engine.Append(e.Chain, new JsonObject { ["type"] = "liveend", ["stream"] = e.Stream, ["outcome"] = "discarded" })); endedKey = ""; }, "Recording discarded."))));
        }
        else endedPanel.Children.Add(Sub("Waiting for your computer’s recording (a few seconds)… It also appears on the channel to publish."));
        endedPanel.Children.Add(st);
    }

    // ---- OBS through one of your computers
    void Obs()
    {
        var S = Svc.S; var st = Status(); var box = new StackPanel { Spacing = 8 };
        var nodes = S.MyNodes().Where(x => x.Perms.Contains('o')).ToList();
        var items = new List<UIElement> { H("With OBS"), Sub("Stream from OBS to one of your computers; it goes live on the channel above.") };
        if (nodes.Count == 0) items.Add(Sub("None of your computers receives OBS yet: run the OpenTuber command on the computer with OBS (Devices)."));
        foreach (var (n, _, _) in nodes) { var node = n; items.Add(Row(Text((string?)S.Info(n)?["name"] ?? "your computer"), Btn("Show OBS settings", () => _ = st.Do("Asking your computer…", async () => ShowObs(node, await S.NodeCtl(node, "obs"), box, st), null!)))); }
        items.Add(box); items.Add(st);
        col.Children.Add(Card([.. items]));
    }
    void ShowObs(string node, JsonObject obs, StackPanel box, TextBlock st)
    {
        var S = Svc.S; st.Say(null); box.Children.Clear();
        var port = (int)(Js.Num(obs["port"]) ?? 1935); var suffix = port == 1935 ? "" : ":" + port;
        var hosts = new List<string?> { (string?)obs["publicHost"], (string?)obs["publicIp"] }.Concat((obs["ips"] as JsonArray ?? []).Select(x => (string?)x)).Append("127.0.0.1").OfType<string>().Where(h => h.Length > 0 && !h.EndsWith(".trycloudflare.com")).Distinct();
        box.Children.Add(Text("In OBS: Settings → Stream → Service “Custom…”", 14));
        foreach (var u in hosts.Select(h => $"rtmp://{h}{suffix}/live")) box.Children.Add(Row(Code("Server: " + u), Btn("Copy", () => Copy(u))));
        var key = (string?)obs["key"] ?? ""; box.Children.Add(Row(Code("Stream key: " + key), Btn("Copy", () => Copy(key))));
        if ((string?)obs["portNote"] is { } pn) box.Children.Add(Sub("⚠ " + pn));
        var yt = new CheckBox { Content = "Also send it to YouTube", IsChecked = Js.IsTrue(obs["youtube"]?["on"]) }; var ytKey = Password(Js.IsTrue(obs["youtube"]?["set"]) ? "•••••••• (saved; type to replace)" : "YouTube stream key");
        box.Children.Add(yt); box.Children.Add(ytKey);
        box.Children.Add(Btn("Save", () => _ = st.Do("Saving…", async () =>
        {
            var y = new JsonObject { ["on"] = yt.IsChecked == true }; if (ytKey.Password.Trim().Length > 0) y["key"] = ytKey.Password.Trim();
            var r = await S.NodeCtl(node, "obs.set", new JsonObject { ["youtube"] = y }); ytKey.Password = ""; st.Say(Js.IsTrue(r["youtube"]?["on"]) ? "Saved: your streams also go to YouTube." : "Saved.");
        }, null!)));
        box.Children.Add(Btn("Go live on this channel from OBS", () => _ = st.Do("Linking…", async () =>
        {
            var ch = chain!.Value(); var owner = S.Engine.Read(e => e.Store.Chains.TryGetValue(ch, out var c) ? c.Owner : null);
            var args = new JsonObject { ["channel"] = ch, ["title"] = title!.Text };
            if (owner != null && S.KeyOf(owner) is { } k) args["channelSig"] = k.Sign($"opentube-obs-channel:{node}:{S.Me.pub}:{ch}");
            var r = await S.NodeCtl(node, "obs.set", args);
            if (!Js.IsTrue(r["linked"]) && owner != null && S.KeyOf(owner) != null) await Task.Run(() => S.Engine.Append(ch, new JsonObject { ["type"] = "relay", ["key"] = node, ["on"] = true }));
        }, "Done: Start Streaming in OBS goes live on this channel."), primary: true));
    }
    static void Copy(string text) { var dp = new Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText(text); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp); }
}
