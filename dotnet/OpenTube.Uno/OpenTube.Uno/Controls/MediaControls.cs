using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Controls;

/// <summary>Moving your media over the network: sharing bytes (a seed) and fetching a file by its hash.</summary>
public static class MediaNet
{
    /// <summary>Share bytes from this device over BitTorrent; their infohash.</summary>
    public static async Task<string> Seed(byte[] data, string name)
    {
        var dir = Path.Combine(Svc.DataFolder, "media-share"); Directory.CreateDirectory(dir);
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        var path = Path.Combine(dir, safe.Length > 0 ? safe : "file"); await File.WriteAllBytesAsync(path, data);
        return (await Svc.Video.Share(path, Path.GetFileName(path))).InfoHash;
    }
    /// <summary>A shared file's bytes: your own copy, or fetched from whoever has it (decrypted when it came with a key).</summary>
    public static async Task<byte[]?> Fetch(JsonObject r)
    {
        var S = Svc.S; var ih = (string)r["ih"]!;
        if (await S.Media.OwnCopy(ih) is { } own) return own;
        var uri = await Svc.Video.Play(ih, (string?)r["name"] ?? "file", Js.Num(r["size"]) ?? 0, _ => { });
        if (uri == null) return null;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var data = await http.GetByteArrayAsync(uri);
        if ((string?)r["key"] is { } k) data = Media.Open(data, k, (string)r["iv"]!);
        return data;
    }
    public static async Task<BitmapImage?> Image(byte[] data)
    {
        try { var b = new BitmapImage(); using var ms = new MemoryStream(data); await b.SetSourceAsync(ms.AsRandomAccessStream()); return b; } catch { return null; }
    }
}

/// <summary>Attached pictures and videos (on a post, a listing…): pictures load as they arrive; a video plays when tapped.</summary>
public sealed partial class MediaView : StackPanel
{
    public MediaView(JsonArray? refs)
    {
        Spacing = 8;
        var row = new WrapRow { Gap = 8 }; Children.Add(row);
        foreach (var r in (refs ?? []).OfType<JsonObject>())
        {
            var name = (string?)r["name"] ?? "file"; var mime = (string?)r["mime"] ?? ""; var size = Js.Num(r["size"]) ?? 0;
            var cell = new StackPanel { Width = 220, Spacing = 2 };
            var pic = new Image { Height = 150, Stretch = Stretch.UniformToFill };
            var frame = new Border { CornerRadius = new CornerRadius(8), Height = 150, Background = (Brush)Application.Current.Resources["BackgroundBrush"], Child = pic };
            if ((string?)r["thumb"] is { Length: > 0 } th) _ = Thumb(pic, th);
            var note = Sub($"{name} · {Svc.Bytes(size)}");
            cell.Children.Add(frame); cell.Children.Add(note);
            if (mime.StartsWith("image/") && size < 8e6) _ = LoadPicture(pic, note, r);
            else if (mime.StartsWith("video/"))
                cell.Children.Add(Btn("▶ Play", () =>
                {
                    note.Text = "Getting it from the network…";
                    _ = Task.Run(async () =>
                    {
                        Uri? u = null; try { u = await Svc.Video.Play((string)r["ih"]!, name, size, _ => { }); } catch { }
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (u == null) { note.Text = "Nobody online has it right now."; return; }
                            var p = new VideoPlayer { Height = 220 }; Children.Add(p); p.Play(u); note.Text = name;
                        });
                    });
                }));
            row.Children.Add(cell);
        }
    }
    static async Task Thumb(Image img, string dataUri) => img.Source ??= await Svc.Thumb(dataUri);
    async Task LoadPicture(Image img, TextBlock note, JsonObject r)
    {
        try
        {
            var data = await MediaNet.Fetch(r);
            if (data == null) { note.Text = "Nobody online has it right now."; return; }
            if (await MediaNet.Image(data) is { } b) img.Source = b;
        }
        catch (Exception e) { note.Text = e.Message; }
    }
}

/// <summary>"📎 Attach" for a composer: pick from My media or add new files; Refs(priv) shares them when you post.</summary>
public sealed partial class AttachBox : StackPanel
{
    readonly List<Media.Item> picked = []; readonly WrapRow chosen = new(); readonly StackPanel lib = new() { Spacing = 6 }; readonly TextBlock msg = Ui.Status();
    bool open;
    public int Count => picked.Count;
    public AttachBox()
    {
        Spacing = 6;
        Children.Add(Row(Btn("📎 Attach", () => { open = !open; _ = Draw(); }), chosen));
        Children.Add(lib); Children.Add(msg);
    }
    void DrawChosen() { chosen.Children.Clear(); foreach (var it in picked) chosen.Children.Add(Sub("📎 " + it.Name[..Math.Min(24, it.Name.Length)])); }
    async Task Draw()
    {
        lib.Children.Clear(); if (!open) return;
        var items = await Svc.S.Media.List();
        var grid = new WrapRow { Gap = 6 };
        foreach (var it in items.Take(60))
        {
            var on = picked.Any(p => p.Id == it.Id);
            var b = new Button { Width = 96, Height = 96, Padding = new Thickness(2), BorderThickness = new Thickness(on ? 3 : 0), BorderBrush = Primary };
            if (it.Thumb is { Length: > 0 } th) { var im = new Image { Stretch = Stretch.UniformToFill }; b.Content = im; _ = SetThumb(im, th); } else b.Content = new TextBlock { Text = it.Name, FontSize = 11, TextWrapping = TextWrapping.Wrap };
            b.Click += (_, _) => { var at = picked.FindIndex(p => p.Id == it.Id); if (at >= 0) picked.RemoveAt(at); else if (picked.Count < 10) picked.Add(it); DrawChosen(); _ = Draw(); };
            grid.Children.Add(b);
        }
        lib.Children.Add(Card(Row(H("My media", 15), Btn("＋ Add files", () => _ = msg.Do("Adding…", async () =>
        {
            foreach (var f in await Files.OpenMedia()) { var it = await Svc.S.Media.Add(f.Name, f.Mime, f.Data, f.Thumb); if (picked.Count < 10) picked.Add(it); }
            DrawChosen(); await Draw();
        }, ""))), items.Count == 0 ? Sub("Your library is empty. Add pictures or videos: they stay private until you attach them.") : grid));
    }
    static async Task SetThumb(Image im, string th) => im.Source = await Svc.Thumb(th);
    /// <summary>Share what's picked (encrypted copies when <paramref name="priv"/>) and give the refs to put on the chain.</summary>
    public async Task<JsonArray> Refs(bool priv)
    {
        var o = new JsonArray();
        foreach (var it in picked) o.Add(await Svc.S.Media.Share(it, priv, MediaNet.Seed));
        return o;
    }
    public void Clear() { picked.Clear(); open = false; DrawChosen(); lib.Children.Clear(); }
}
