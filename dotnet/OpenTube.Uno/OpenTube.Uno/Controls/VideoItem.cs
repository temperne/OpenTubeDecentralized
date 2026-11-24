using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenTube.Core;
using OpenTube.Client;
using OpenTube.Uno.Services;

namespace OpenTube.Uno.Controls;

/// <summary>A video as lists show it: picture, title, channel, views and age. Opens the watch page.</summary>
public partial class VideoItem : ObservableObject
{
    public required string Chain { get; init; }
    public required string Hash { get; init; }
    public required string Title { get; init; }
    public required string Channel { get; init; }
    public required string Meta { get; init; }
    public bool Live { get; init; }
    public string? Stream { get; init; }
    [ObservableProperty] public partial BitmapImage? Picture { get; set; }

    public static VideoItem From(string chain, string channel, JsonObject b, bool live = false)
    {
        var p = b["payload"]!;
        var item = new VideoItem
        {
            Chain = chain, Hash = (string)b["hash"]!, Title = (string?)p["title"] ?? "Untitled", Channel = channel, Live = live, Stream = (string?)p["stream"],
            Meta = live ? $"🔴 Live · started {Svc.Ago(Js.Num(b["ts"]) ?? 0)}" : $"{Svc.Views(Svc.S.Views((string)b["hash"]!))} · {Svc.Ago(Js.Num(b["ts"]) ?? 0)}",
        };
        _ = item.Load((string?)p["thumb"]);
        return item;
    }
    async Task Load(string? thumb) => Picture = await Svc.Thumb(thumb);
}
