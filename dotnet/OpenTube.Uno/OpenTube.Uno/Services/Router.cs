using OpenTube.Uno.Pages;

namespace OpenTube.Uno.Services;

/// <summary>
/// Opens the screen for a link in the network's form (notifications, mentions, shared links): "#/v/{chain}/{video}",
/// "c/{chain}[/{tab}]", "messages/{user}", "posts/{month}[/{post}]", "square/…", "friends", "you/…", and so on.
/// </summary>
public static class Router
{
    public static void Go(string href)
    {
        var h = href.StartsWith("#/") ? href[2..] : href.TrimStart('/');
        var p = h.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0) return;
        switch (p[0])
        {
            case "v" when p.Length >= 3: App.Open(typeof(WatchPage), new WatchArgs(p[1], p[2])); break;
            case "live" when p.Length >= 3: App.Open(typeof(LivePage), new LiveArgs(p[1], p[2])); break;
            case "c" when p.Length >= 2: ChannelPage.Open(p[1], p.Length >= 3 ? p[2] : ""); break;
            case "messages": App.Open(typeof(MessagesPage), p.Length >= 2 ? p[1] : null); break;
            case "posts": case "square": App.Open(typeof(PostsPage), p.Length >= 3 ? p[1] + "/" + p[2] : p.Length == 2 ? p[1] : null); break;
            case "friends": App.Open(typeof(FriendsPage)); break;
            case "you": App.Open(typeof(YouPage), p.Length >= 2 ? p[1] : "profile"); break;
            case "wallet": App.Open(typeof(YouPage), "wallet"); break;
            case "notifications": App.Open(typeof(NotificationsPage), p.Length >= 2 ? p[1] : null); break;
            case "upload": App.Open(typeof(UploadPage), p.Length >= 2 ? p[1] : null); break;
            case "devices": case "me": App.Open(typeof(DevicesPage)); break;
            case "golive": App.Open(typeof(GoLivePage), p.Length >= 2 ? p[1] : null); break;
            case "network": App.Open(typeof(NetworkPage)); break;
            case "u" when p.Length >= 2: ProfilePage.Open(p[1]); break;
            case "market" when p.Length >= 3 && p[1] == "item": App.Open(typeof(MarketItemPage), p[2]); break;
            case "market": App.Open(typeof(MarketPage), p.Length >= 2 ? p[1] : null); break;
            case "exchange": App.Open(typeof(ExchangePage), p.Length >= 2 ? p[1] : null); break;
            case "orgs": App.Open(typeof(OrgsPage)); break;
            default: App.Open(typeof(ComingPage), p[0]); break;
        }
    }
}
