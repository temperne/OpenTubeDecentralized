using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>Your channel's ads: its budget, where they run, making an ad from a video, and stopping one.</summary>
public sealed partial class ChannelPage
{
    Files.Picked? adFile;
    void Advertise(Derived d)
    {
        var S = Svc.S; var running = S.WhereAdsRun(id);
        var where = new StackPanel { Spacing = 4 };
        if (running.Count == 0) where.Children.Add(Sub("Nowhere yet. A channel or list owner must sponsor this channel (Market → Ad board) for your ads to run."));
        foreach (var c in running) { var cc = c; where.Children.Add(Btn((string?)S.Genesis(cc)?["payload"]?["name"] ?? cc[..8], () => Open(cc))); }
        body.Children.Add(Card(Row(Sub("Ad budget (this channel’s coins):"), Text(Trade.Coins(S.Balance(d.Owner!)), 15)), Sub("Running on:"), where));

        var title = Input("Title", "", 140); var url = Input("Link: https://… (optional)", "", 300);
        var cat = Pick([("", "Choose what it’s for…"), .. Market.Categories.Where(c => c.Key is not "adviews").Select(c => (c.Key, c.Name))]); cat.MinWidth = 220;
        var tags = Input("Tags, e.g. running, shoes (up to 8)", "", 200);
        var per = Input("Coins per verified view", "0.20", 20); var maxViews = Input("Maximum views", "100", 7); var secs = Input($"Length in seconds (1–{Ads.MaxDuration})", "", 3);
        var confirm = new CheckBox { Content = "Pay a view only when another viewer watched it in sync and confirms it (safer against bots, but on a small network most views won’t pay)" };
        var fileLine = Sub(adFile != null ? $"{adFile.Name} · {Svc.Bytes(adFile.Size)}" : "No video chosen.");
        body.Children.Add(Card(H("Create an ad", 16),
            Sub("Ads are paid from this channel’s coins. Each verified view pays the price per view: half to the viewer, half to the creator of the video it ran with. Add coins to this channel from the Wallet (send to its ID)."),
            Row(Btn("Choose the ad video", () => _ = status.Do("Reading the file…", async () => { if (await Files.OpenVideo((_, _) => { }) is { } f) { adFile = f; fileLine.Text = $"{f.Name} · {Svc.Bytes(f.Size)}"; if (title.Text.Length == 0) title.Text = Path.GetFileNameWithoutExtension(f.Name); } }, "")), fileLine),
            title, url, cat, tags, Row(per, maxViews, secs), confirm,
            Btn("Publish ad", () =>
            {
                if (adFile is not { } f) { status.Say("Choose an ad video"); return; }
                _ = status.Do("Sharing the ad file…", async () =>
                {
                    var meta = await Svc.Video.Share(f.Path, f.Name);
                    var thumb = await Svc.Video.Thumbnail(f.Path, meta.InfoHash, f.Name) ?? "";
                    var tagList = tags.Text.ToLowerInvariant().Split([' ', ',', '#'], StringSplitOptions.RemoveEmptyEntries).Select(t => System.Text.RegularExpressions.Regex.Replace(t, "[^a-z0-9-]", "")).Where(t => t.Length > 0).Distinct().Take(8).ToList();
                    await S.CreateAd(id, meta.InfoHash, f.Name, f.Size, int.TryParse(secs.Text, out var sec) ? sec : 0, title.Text, url.Text, cat.Value(), tagList, thumb, Trade.Units(per.Text), int.TryParse(maxViews.Text, out var mv) ? mv : 100, confirm.IsChecked == true);
                    adFile = null; shown = ""; Refresh();
                }, "Ad published. Channels and lists that sponsor you start running it (keep the app open, or the file on one of your computers, so viewers can get it).");
            }, primary: true, id: "ad-publish"), status));

        var list = new List<UIElement> { H("Your ads", 16) };
        if (d.Ads.Count == 0) list.Add(Sub("None yet."));
        foreach (var b in d.Ads)
        {
            var t = b["payload"]!["terms"]!; var tid = Ads.TermsId(t); var hash = (string)b["hash"]!;
            list.Add(Row(Text((string?)b["payload"]?["title"] ?? "", 14), Sub($"{S.AdViews(tid)} of {t["maxViews"]} views · {Trade.Coins(Js.Num(t["perView"]) ?? 0)} coins per view · {t["duration"]} s{((Js.Num(t["witnesses"]) ?? 0) > 0 ? " · confirmed views only" : "")}"),
                Btn("Stop ad", () => Run(() => S.Hide(id, hash), "Ad stopped: it no longer runs."))));
        }
        body.Children.Add(Card([.. list]));
    }
}
