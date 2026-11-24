using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>A tag: the videos and posts with it on the channels this device knows, and "Not interested".</summary>
public sealed partial class TagPage : DataPage
{
    string tag = ""; bool? muted;
    public TagPage() : base("Tag", 1200) { }
    protected override void Param(object? p) { if (p is string s) tag = Uri.UnescapeDataString(s).TrimStart('#').ToLowerInvariant(); }
    protected override string Key()
    {
        var S = Svc.S;
        if (muted == null) _ = Task.Run(async () => { try { muted = (await S.Social.Muted()).Tags.Contains(tag); } catch { muted = false; } });
        return $"{tag}|{muted}|{S.Tagged(tag).Count}|{S.TaggedPosts(tag).Count}";
    }
    protected override void Build()
    {
        var S = Svc.S;
        Col.Children.Add(Row(H("#" + tag, 22), Btn(muted == true ? "Show this tag again" : "Not interested", () => _ = Status.Do("Saving…", async () => { muted = muted != true; await S.Social.SetMuted("tags", tag, muted == true); Redraw(); }, ""))));
        Col.Children.Add(Sub(muted == true ? "You said you’re not interested in this tag, so its videos and ads are hidden elsewhere." : "Videos with this tag on the channels this device knows. Subscribe to more channels to see more."));
        Col.Children.Add(Status);
        var found = S.Tagged(tag);
        if (found.Count == 0) Col.Children.Add(Sub("No videos with this tag yet."));
        else Col.Children.Add(new VideoGrid { Items = found.Select(x => VideoItem.From(x.Item1, x.Item2, (JsonObject)x.Item3.DeepClone())).ToList() });
        var posts = S.TaggedPosts(tag);
        if (posts.Count == 0) return;
        var ps = new List<UIElement> { H("Posts") };
        foreach (var (c, kind, b) in posts)
        {
            var d = S.Chain(c); var a = S.AuthorOf((string)b["author"]!, kind == "group" ? d : null, (string?)b["payload"]?["name"] ?? "");
            var month = (string?)S.Genesis(c)?["payload"]?["month"]; var hash = (string)b["hash"]!;
            ps.Add(new StackPanel { Spacing = 2, Children = { Sub($"{a.Name} · {Svc.Ago(Js.Num(b["ts"]) ?? 0)}{(kind == "group" ? " · in " + d?.Profile["name"] : "")}"), Text((string?)b["payload"]?["text"] ?? "", 14),
                Btn("Open", () => { if (kind == "square" && month != null) App.Open(typeof(PostsPage), month + "/" + hash); else ChannelPage.Open(c); }) } });
        }
        Col.Children.Add(Card([.. ps]));
    }
}

/// <summary>A registered name (@channel, ~list, @@person): opens what holds it, or says nobody does yet.</summary>
public sealed partial class NamePage : DataPage
{
    string full = ""; bool went;
    public NamePage() : base("Name") { }
    protected override void Param(object? p) { if (p is string s) full = Uri.UnescapeDataString(s).ToLowerInvariant(); }
    protected override string Key() => $"{full}|{Svc.S.NameHolder(full)}";
    protected override void Build()
    {
        var S = Svc.S; var chain = S.NameHolder(full);
        if (chain != null && !went)
        {
            went = true;
            if (full.StartsWith("@@")) ProfilePage.Open(S.Engine.Read(e => e.Store.Ledger.Resolve((string?)S.Genesis(chain)?["author"] ?? "")));
            else ChannelPage.Open(chain);
            Col.Children.Add(Sub("Opening " + full + "…")); return;
        }
        Col.Children.Add(H(full, 22));
        Col.Children.Add(Text($"No {(full.StartsWith("@@") ? "person" : full.StartsWith('@') ? "channel" : "list")} is called {full} (or the network hasn’t synced yet)."));
        Col.Children.Add(Row(Btn("Explore", () => App.Open(typeof(ExplorePage))), Btn("Create one", () => App.Open(typeof(YouPage), "channels"))));
    }
}
