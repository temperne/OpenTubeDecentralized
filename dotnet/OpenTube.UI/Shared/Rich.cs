using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using OpenTube.Client;
using OpenTube.Core;

namespace OpenTube.UI.Shared;

/// <summary>User text, formatted the way the JavaScript page does it (and nothing is HTML): **bold**, *italic*,
/// `code`, lines starting "- " as a list, [label](https://…) and bare links, #tags, @channel ~list @@person names.</summary>
public sealed partial class Rich : ComponentBase
{
    [Inject] public ClientSession S { get; set; } = null!;
    [Parameter] public string? Text { get; set; }
    [Parameter] public string Tag { get; set; } = "div";
    /// <summary>Who wrote it: a good-link vote pays them.</summary>
    [Parameter] public string? Author { get; set; }
    readonly HashSet<string> votedHere = [];

    [GeneratedRegex(@"\[([^\]\n]{1,80})\]\((https?://[^\s)]{1,300})\)|(https?://[^\s<>()]{2,300})|\*\*([^*\n]{1,200})\*\*|\*([^*\n]{1,200})\*|`([^`\n]{1,200})`|(^|[^\w@#])(@@|@|~|#)([a-z0-9][a-z0-9._-]{1,29})", RegexOptions.IgnoreCase)]
    private static partial Regex Inline();
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,23}$")] private static partial Regex TagRe();

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        int seq = 0;
        b.OpenElement(seq++, Tag); b.AddAttribute(seq++, "class", "rich");
        var lines = (Text ?? "").Split('\n'); bool inList = false;
        foreach (var line in lines)
        {
            var item = Regex.Match(line, @"^\s*[-*] ");
            if (item.Success)
            {
                if (!inList) { b.OpenElement(seq++, "ul"); inList = true; }
                b.OpenElement(seq++, "li"); InlineInto(b, ref seq, line[item.Length..]); b.CloseElement(); continue;
            }
            if (inList) { b.CloseElement(); inList = false; }
            b.OpenElement(seq++, "div");
            if (line.Length == 0) { b.OpenElement(seq++, "br"); b.CloseElement(); } else InlineInto(b, ref seq, line);
            b.CloseElement();
        }
        if (inList) b.CloseElement();
        b.CloseElement();
    }
    void Link(RenderTreeBuilder b, ref int seq, string href, string text, string? cls = null, bool external = false)
    {
        b.OpenElement(seq++, "a"); b.AddAttribute(seq++, "href", href); if (cls != null) b.AddAttribute(seq++, "class", cls);
        if (external) { b.AddAttribute(seq++, "target", "_blank"); b.AddAttribute(seq++, "rel", "noopener nofollow ugc"); }
        b.AddContent(seq++, text); b.CloseElement();
    }
    /// <summary>A link with its domain's reputation across the network, and 👍/👎 (paid: up pays whoever shared it, down is burned).</summary>
    void Chip(RenderTreeBuilder b, ref int seq, string url, string label)
    {
        var dom = Tx.DomainOf(url);
        b.OpenElement(seq++, "span"); b.AddAttribute(seq++, "class", "linkchip");
        Link(b, ref seq, url, label, "ext", true);
        if (dom != null)
        {
            var (up, down) = S.Engine.Read(e => e.Store.Ledger.Domains.TryGetValue(dom, out var r) ? r : (0, 0));
            var net = up - down;
            b.OpenElement(seq++, "span"); b.AddAttribute(seq++, "class", "sub"); b.AddAttribute(seq++, "title", $"{dom}: {up} up, {down} down across the network");
            b.AddContent(seq++, $" {dom}{(up + down > 0 ? (net >= 0 ? " +" : " ") + net : "")}"); b.CloseElement();
            var voted = votedHere.Contains(url) || S.Engine.Read(e => e.Store.Ledger.LinkVotes.Contains(S.Me.pub + "|" + url));
            if (!voted && S.Ready)
                foreach (var v in new[] { 1, -1 })
                {
                    var dir = v;
                    b.OpenElement(seq++, "button"); b.AddAttribute(seq++, "class", "tagx");
                    b.AddAttribute(seq++, "title", dir > 0 ? "Good link (pays whoever shared it)" : "Bad link (costs coins, burned)");
                    b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () => Vote(url, dom, dir)));
                    b.AddContent(seq++, dir > 0 ? "👍" : "👎"); b.CloseElement();
                }
        }
        b.CloseElement();
    }
    async Task Vote(string url, string dom, int v)
    {
        votedHere.Add(url);
        var x = new System.Text.Json.Nodes.JsonObject { ["url"] = url, ["domain"] = dom, ["v"] = v, ["amt"] = S.Price("like") };
        if (Author != null && !S.IsMine(Author)) x["to"] = Author;
        try { await S.Engine.SubmitTx(new System.Text.Json.Nodes.JsonObject { ["linkvote"] = x }, S.Me); }
        catch { votedHere.Remove(url); }
    }
    void InlineInto(RenderTreeBuilder b, ref int seq, string text)
    {
        int last = 0;
        foreach (Match m in Inline().Matches(text))
        {
            if (m.Index > last) b.AddContent(seq++, text[last..m.Index]);
            var g = m.Groups;
            if (g[1].Success) Chip(b, ref seq, g[2].Value, g[1].Value);
            else if (g[3].Success)
            {
                var url = g[3].Value.TrimEnd('.', ',', ';', ':', '!', '?');
                Chip(b, ref seq, url, url.Replace("https://", "").Replace("http://", "")[..Math.Min(60, url.Length - (url.StartsWith("https") ? 8 : 7))]);
                if (url.Length < g[3].Length) b.AddContent(seq++, g[3].Value[url.Length..]);
            }
            else if (g[4].Success) { b.OpenElement(seq++, "b"); b.AddContent(seq++, g[4].Value); b.CloseElement(); }
            else if (g[5].Success) { b.OpenElement(seq++, "i"); b.AddContent(seq++, g[5].Value); b.CloseElement(); }
            else if (g[6].Success) { b.OpenElement(seq++, "code"); b.AddContent(seq++, g[6].Value); b.CloseElement(); }
            else if (g[8].Success)
            {
                if (g[7].Length > 0) b.AddContent(seq++, g[7].Value);
                var sig = g[8].Value; var word = g[9].Value.ToLowerInvariant().TrimEnd('.', '_', '-'); var rest = g[9].Value[word.Length..];
                if (sig == "#") { if (TagRe().IsMatch(word)) Link(b, ref seq, "tag/" + word, "#" + word, "tag"); else b.AddContent(seq++, "#" + g[9].Value); }
                else
                {
                    var hit = S.Engine.Read(e => e.Store.Ledger.Names.TryGetValue(sig + word, out var n) ? n : null);
                    var chain = (string?)hit?["chain"];
                    if (chain == null) b.AddContent(seq++, sig + word);
                    else if (sig == "@@") Link(b, ref seq, "u/" + S.Engine.Read(e => e.Store.Ledger.Resolve(S.Genesis(chain)?["author"]?.GetValue<string>() ?? (string?)hit!["key"] ?? "")), sig + word, "person");
                    else Link(b, ref seq, "c/" + chain, sig + word, "person");
                }
                if (rest.Length > 0) b.AddContent(seq++, rest);
            }
            last = m.Index + m.Length;
        }
        if (last < text.Length) b.AddContent(seq++, text[last..]);
    }
}
