namespace OpenTube.UI;

/// <summary>
/// Where a notification leads: the C# page for it (videos, live streams, channels), else the JavaScript page's section
/// (the parts not ported yet). Also carries a tap on a system notification (phone) to the pages.
/// </summary>
public static class AppNav
{
    public static string LinkFor(string href, string site)
    {
        var h = href.StartsWith("#/") ? href[2..] : href.TrimStart('/');
        var parts = h.Split('/');
        if (parts.Length >= 3 && parts[0] is "v" or "live") return $"{parts[0]}/{parts[1]}/{parts[2]}";
        if (parts.Length >= 2 && parts[0] == "c") return $"c/{parts[1]}";
        if (parts[0] == "messages") return parts.Length >= 2 && parts[1].Length > 0 ? $"messages/{parts[1]}" : "messages";
        return site.TrimEnd('/') + "/#/" + h;   // not in the C# pages yet
    }
    /// <summary>A notification the person tapped (the phone app), to open once the pages are up.</summary>
    public static string? Pending;
    public static event Action<string>? Go;
    public static void Open(string href) { Pending = href; Go?.Invoke(href); }
}
