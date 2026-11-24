using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

public sealed record LiveArgs(string Chain, string Stream);

/// <summary>
/// A live stream: the player (the stream arrives through the network, as on the website), who's watching, the chat,
/// and after it ends its statistics and the recording if it was published.
/// </summary>
public sealed partial class LivePage : Page
{
    string chain = "", stream = ""; LiveWatch? w; bool started; string? shownChat;
    readonly VideoPlayer player = new() { Height = 220 };
    readonly TextBlock title = H("", 20), status = Sub(""), viewers = Sub(""), info = Sub("");
    readonly StackPanel chat = new() { Spacing = 6 }, people = new() { Spacing = 4 }, after = new() { Spacing = 8 };
    readonly TextBox say = new() { PlaceholderText = "Say something…", MaxLength = 2000 };
    readonly TextBlock msg = Status();
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(2) };
    public LivePage()
    {
        Background = Application.Current.Resources["BackgroundBrush"] as Brush;
        var (scroll, col) = Ui.Page(900);
        var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(player); Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        Content = Ui.WithBack("Live", grid);
        SizeChanged += (_, e) => player.Height = Math.Min(e.NewSize.Width * 9 / 16, e.NewSize.Height * 0.45);
        say.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Send(); };
        col.Children.Add(status); col.Children.Add(title); col.Children.Add(info); col.Children.Add(viewers); col.Children.Add(after);
        col.Children.Add(Card(H("Chat", 16), chat, Row(say, Btn("Send", Send, primary: true)), msg));
        col.Children.Add(Card(H("People here", 16), people));
        tick.Tick += (_, _) => Fill();
        Loaded += (_, _) => { tick.Start(); Fill(); };
        Unloaded += (_, _) => { tick.Stop(); player.Pause(); };
    }
    protected override void OnNavigatedTo(NavigationEventArgs e) { if (e.Parameter is LiveArgs a) { chain = a.Chain; stream = a.Stream; } }
    JsonObject? Find(Derived? d) => d?.Streams.FirstOrDefault(b => (string?)b["payload"]?["stream"] == stream || (string?)b["hash"] == stream);
    static string Dur(double secs) => secs < 60 ? $"{secs:0} s" : secs < 3600 ? $"{Math.Round(secs / 60)} min" : $"{secs / 3600:0.0} h";
    static string Num(JsonNode? n) => ((long)(Js.Num(n) ?? 0)).ToString("N0");
    void Fill()
    {
        var S = Svc.S; if (S == null || !S.Ready) return;
        if (started) { var (ps, pos, _) = player.Status(); Console.WriteLine($"[live] player {ps} at {pos:0.0} s · {w?.Status} · {w?.Bytes} bytes"); }
        var d = S.Chain(chain); var lb = Find(d);
        if (d == null || lb == null) { status.Text = "Loading this stream from the network…"; return; }
        var p = lb["payload"]!; var st = (string?)p["stream"] ?? ""; var ended = d.LiveEnds.ContainsKey(st) || w?.Ended == true;
        title.Text = (ended ? "" : "🔴 LIVE · ") + ((string?)p["title"] ?? "Live");
        info.Text = $"{(string?)d.Profile["name"]} · started {Svc.Ago(Js.Num(lb["ts"]) ?? 0)}";
        status.Text = (ended ? "This stream has ended." : w?.Status ?? "Joining…") + (w != null && w.Bytes > 0 ? $" · {Svc.Bytes(w.Bytes)} received" : "");
        var v = w?.Viewers;
        viewers.Text = v != null && !ended ? $"👁 {Num(v["viewers"])} watching · peak {Num(v["peak"] ?? v["viewers"])} · {Num(v["seenCount"])} have watched" : "";
        people.Children.Clear();
        foreach (var k in (v?["now"] as JsonArray ?? []).Select(x => (string?)x).OfType<string>()) people.Children.Add(Text(S.NameOf(k), 13));
        if (people.Children.Count == 0) people.Children.Add(Sub(ended ? "The stream is over." : "Nobody signed in is watching right now."));
        after.Children.Clear();
        if (ended)
        {
            if (d.Recordings.GetValueOrDefault(st) is { } rec) after.Children.Add(Btn("Watch the recording", () => App.Open(typeof(WatchPage), new WatchArgs(chain, (string)rec["hash"]!)), primary: true));
            else after.Children.Add(Sub("If the streamer publishes the recording, it appears with the channel’s videos."));
            if (d.LiveStats.GetValueOrDefault(st) is JsonObject stats)
            {
                var secs = Js.Num(stats["secs"]) ?? 0; var unique = Js.Num(stats["unique"]) ?? 0;
                after.Children.Add(Card(H("Stream statistics", 15), Text($"{Num(stats["peak"])} peak viewers · {unique} unique · {Dur(secs)} watched in total · {(unique > 0 ? Dur(Math.Round(secs / unique)) : "–")} per viewer", 14), Sub("Public on OpenTube: anyone can see these.")));
            }
        }
        // the chat: comments on the live block
        var hash = (string)lb["hash"]!;
        var msgs = d.CommentsFor(hash).Where(c => !d.Hidden.Contains((string)c["hash"]!)).OrderBy(c => Js.Num(c["ts"]) ?? 0).ToList();
        var key = string.Join(",", msgs.Select(c => (string?)c["hash"]));
        if (key != shownChat)
        {
            shownChat = key; chat.Children.Clear();
            if (msgs.Count == 0) chat.Children.Add(Sub("No messages yet."));
            foreach (var c in msgs.TakeLast(200)) { var a = S.AuthorOf((string)c["author"]!, d, (string?)c["payload"]?["name"] ?? ""); chat.Children.Add(Text($"{a.Name}{(a.Owner ? " (host)" : "")}: {(string?)c["payload"]?["text"]}", 14)); }
        }
        var can = S.IsMine(d.Owner) || d.CanComment(S.Me.pub, hash); say.IsEnabled = can; if (!can) say.PlaceholderText = "Chat isn’t open to you here";
        if (!started && !d.LiveEnds.ContainsKey(st))
        {
            started = true; w = S.Watch(chain, (JsonObject)lb.DeepClone());
            _ = Svc.Video.PlayLive(w).ContinueWith(t => DispatcherQueue.TryEnqueue(() => { if (t.Result is { } u) player.Play(u); else status.Text = "This device can’t play live streams yet."; }));
        }
    }
    void Send()
    {
        var text = say.Text.Trim(); if (text.Length == 0) return;
        var lb = Find(Svc.S.Chain(chain)); if (lb == null) return;
        _ = msg.Do("Sending…", async () => { await Svc.S.Comment(chain, (string)lb["hash"]!, text); say.Text = ""; msg.Say(null); });
    }
}
