using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static OpenTube.Core.Js;

namespace OpenTube.Core;

/// <summary>
/// createLiveHub: every node is a live hub (at /live). A broadcaster's browser sends WebRTC to a tree of viewers —
/// members of the channel first, right under the broadcaster; everyone else below members; each viewer relays to a few
/// more. The hub introduces parents and children and passes their WebRTC signalling between them. It also relays the
/// broadcaster's signed segments (~1 s pieces) to viewers whose WebRTC fails and to members (who save the stream), keeps
/// the stream's bundled chunks, and reports viewers, watch time and connection quality (public statistics).
/// </summary>
public sealed class LiveHub
{
    const double KeepMs = 60000, MaxSeg = 1 << 20, SrcSlots = 4, MaxSlots = 6, MaxDepth = 8, MaxDcSlots = 32, AliveMs = 15000, StaleMs = 45000;
    public Func<string, string?> OwnerOf = _ => null;               // chain -> its owner key
    public Func<string, string, string?> SignerOf = (_, _) => null;  // (chain, stream) -> a computer allowed to send it
    public Func<string, string, bool> IsJoined = (_, _) => false;    // (user, chain) -> joined it
    public Func<string, string?> UserOf = _ => null;                 // node key -> its owner (a computer counts as its owner)
    public Func<JsonObject, string>? IngestTicket;                   // OBS: a stream key for this stream (null: not offered here)

    sealed class Sock { public required Action<string> Send; public required string Nonce; public HashSet<string> Keys = []; public HashSet<string> Watching = []; }
    sealed class Node
    {
        public string? Parent; public HashSet<string> Children = [], DcKids = []; public double Slots, DcSlots, Depth; public bool Member, Media = true, Relay;
        public string Mode = "pending"; public double Seen = Now(); public string? Who; public JsonObject? Q; public double QAt;
    }
    sealed class Seen { public double First, Last, Secs; }
    sealed class Stream
    {
        public required string Id, Chain, Src, Mime; public bool Media = true; public double Started, Peak;
        public JsonObject? Seg0; public List<JsonObject> Segs = []; public List<JsonObject>? Chunks; public Dictionary<string, Seen> SeenBy = [];
        public Dictionary<string, Node> Nodes = [];
    }
    readonly Dictionary<string, Sock> sockets = [];
    readonly Dictionary<string, Stream> streams = [];
    readonly object gate = new();
    readonly Timer timer;
    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public LiveHub() { timer = new Timer(_ => Tick(), null, (int)AliveMs, (int)AliveMs); }

    void Out(string sid, JsonObject m) { if (sockets.TryGetValue(sid, out var s)) try { s.Send(m.ToJsonString()); } catch { } }
    bool MemberOf(string sid, string chain) => sockets.TryGetValue(sid, out var s) && s.Keys.Any(k => { var u = UserOf(k); var owner = OwnerOf(chain); return new[] { k, u }.Any(x => x != null && (x == owner || IsJoined(x, chain))); });

    JsonObject Counts(Stream st)
    {
        var now = Now(); int members = 0, rtc = 0, seg = 0, dc = 0, viewers = 0;
        foreach (var (sid, n) in st.Nodes) if (sid != st.Src && now - n.Seen < StaleMs && !n.Relay) viewers++;
        foreach (var (sid, n) in st.Nodes) if (sid != st.Src) { if (n.Member) members++; if (n.Mode == "rtc") rtc++; else if (n.Mode == "seg") seg++; else if (n.Mode == "dc") dc++; }
        st.Nodes.TryGetValue(st.Src, out var src);
        var nowKeys = st.Nodes.Where(kv => kv.Key != st.Src && !kv.Value.Relay && kv.Value.Who != null && now - kv.Value.Seen < StaleMs).Select(kv => kv.Value.Who!).Distinct().Take(100).ToList();
        double SecsOf(string k, Seen x) => x.Secs + (nowKeys.Contains(k) ? Math.Min(60, (now - x.Last) / 1000) : 0);
        var seen = st.SeenBy.OrderByDescending(kv => kv.Value.Last).Take(100).Select(kv => (JsonNode?)new JsonObject { ["k"] = kv.Key, ["first"] = kv.Value.First, ["last"] = kv.Value.Last, ["secs"] = Ledger.JsRound(SecsOf(kv.Key, kv.Value)) }).ToArray();
        st.Peak = Math.Max(st.Peak, viewers);
        var qs = st.Nodes.Where(kv => kv.Key != st.Src && !kv.Value.Relay && kv.Value.Q != null && now - kv.Value.QAt < StaleMs).Select(kv => kv.Value.Q!).ToList();
        var ks = qs.Select(q => Num(q["kbps"])).Where(k => k != null).Select(k => k!.Value).Order().ToList();
        var quality = new JsonObject { ["n"] = qs.Count, ["poor"] = qs.Count(q => Num(q["stalls"]) > 0 || (Num(q["buf"]) is double b && b < 1)), ["kbps"] = ks.Count > 0 ? ks[ks.Count >> 1] : null };
        var watchSecs = Ledger.JsRound(st.SeenBy.Sum(kv => SecsOf(kv.Key, kv.Value)));
        return new JsonObject
        {
            ["viewers"] = viewers, ["peak"] = st.Peak, ["watchSecs"] = watchSecs, ["quality"] = quality, ["now"] = new JsonArray(nowKeys.Select(k => (JsonNode?)k).ToArray()), ["seenCount"] = st.SeenBy.Count,
            ["seen"] = new JsonArray(seen), ["members"] = members, ["rtc"] = rtc, ["seg"] = seg, ["dc"] = dc, ["direct"] = (src?.Children.Count ?? 0) + (src?.DcKids.Count ?? 0),
        };
    }
    void Report(Stream st) { var c = Counts(st); c["t"] = "viewers"; c["stream"] = st.Id; Out(st.Src, c); }
    void SendSegs(string sid, Stream st) { if (st.Seg0 != null) Out(sid, new JsonObject { ["t"] = "seg", ["seg"] = st.Seg0.DeepClone() }); foreach (var g in st.Segs) if (Num(g["n"]) != 0) Out(sid, new JsonObject { ["t"] = "seg", ["seg"] = g.DeepClone() }); }
    void Detach(Stream st, string sid)
    {
        if (!st.Nodes.TryGetValue(sid, out var n) || n.Parent == null) return;
        if (st.Nodes.TryGetValue(n.Parent, out var p)) { p.Children.Remove(sid); p.DcKids.Remove(sid); Out(n.Parent, new JsonObject { ["t"] = "unchild", ["stream"] = st.Id, ["peer"] = sid }); }
        Out(sid, new JsonObject { ["t"] = "unparent", ["stream"] = st.Id, ["peer"] = n.Parent }); n.Parent = null;
    }
    HashSet<string> Subtree(Stream st, string sid, HashSet<string>? acc = null)
    {
        acc ??= []; acc.Add(sid);
        if (st.Nodes.TryGetValue(sid, out var x)) foreach (var c in x.Children.Concat(x.DcKids).ToList()) Subtree(st, c, acc);
        return acc;
    }
    static List<string> KidsOf(Node n) { var k = n.Children.Concat(n.DcKids).ToList(); n.Children.Clear(); n.DcKids.Clear(); return k; }

    void Tick()
    {
        lock (gate)
        {
            foreach (var st in streams.Values.ToList())
            {
                foreach (var (sid, n) in st.Nodes.ToList()) if (sid != st.Src && Now() - n.Seen > StaleMs) Leave(st, sid);
                foreach (var (sid, n) in st.Nodes) if (sid != st.Src && !n.Member && MemberOf(sid, st.Chain))
                {   // their membership arrived after they started watching
                    n.Member = true; Out(sid, new JsonObject { ["t"] = "watching", ["stream"] = st.Id, ["mime"] = st.Mime, ["member"] = true }); SendSegs(sid, st);
                    foreach (var c in (st.Chunks ?? []).TakeLast(600)) Out(sid, new JsonObject { ["t"] = "lchunk", ["stream"] = st.Id, ["chunk"] = c.DeepClone() });
                }
                if (!streams.ContainsKey(st.Id)) continue;
                var cnt = Counts(st); cnt["t"] = "viewers"; cnt["stream"] = st.Id;
                foreach (var sid in st.Nodes.Keys) Out(sid, cnt);
            }
        }
    }

    void Assign(Stream st, string sid)
    {
        if (!st.Nodes.TryGetValue(sid, out var n)) return;
        var below = Subtree(st, sid); var src = st.Nodes[st.Src];
        void Place(string pid, Node p, bool dc)
        {
            n.Parent = pid; n.Depth = p.Depth + 1; n.Mode = dc ? "dc" : "rtc"; (dc ? p.DcKids : p.Children).Add(sid);
            foreach (var c in below) if (c != sid && st.Nodes.TryGetValue(c, out var x) && x.Parent != null && st.Nodes.TryGetValue(x.Parent, out var px)) x.Depth = px.Depth + 1;
            var a = new JsonObject { ["t"] = "parent", ["stream"] = st.Id, ["peer"] = pid }; if (dc) a["dc"] = true; Out(sid, a);
            var b = new JsonObject { ["t"] = "child", ["stream"] = st.Id, ["peer"] = sid }; if (dc) b["dc"] = true; Out(pid, b);
            Report(st);
        }
        if (st.Media && n.Media)
        {   // the video itself, over WebRTC
            bool Free(Node x, string id) => (x.Mode is "src" or "rtc") && x.Children.Count < x.Slots && x.Depth < MaxDepth && !below.Contains(id);
            string? bumped = null;
            if (n.Member && src.Children.Count >= src.Slots)
            {   // members first: move a non-member off the broadcaster
                bumped = src.Children.FirstOrDefault(c => st.Nodes.TryGetValue(c, out var y) && !y.Member);
                if (bumped != null) Detach(st, bumped);
            }
            var cands = st.Nodes.Where(kv => kv.Key != sid && Free(kv.Value, kv.Key)).Select((kv, i) => (kv, i)).OrderBy(t => n.Member ? -((t.kv.Key == st.Src) ? 1 : 0) : -(t.kv.Value.Member ? 1 : 0))
                .ThenBy(t => t.kv.Value.Depth).ThenBy(t => t.kv.Value.Children.Count).ThenBy(t => t.i).Select(t => t.kv).ToList();
            if (cands.Count > 0) { Place(cands[0].Key, cands[0].Value, false); if (bumped != null) Assign(st, bumped); return; }
            if (bumped != null) Assign(st, bumped);
        }
        bool FreeDc(Node x, string id) => (x.Mode is "src" or "dc" or "seg") && x.DcKids.Count < x.DcSlots && x.Depth < MaxDepth && !below.Contains(id);
        var dcs = st.Nodes.Where(kv => kv.Key != sid && FreeDc(kv.Value, kv.Key)).Select((kv, i) => (kv, i)).OrderBy(t => t.kv.Value.Depth).ThenByDescending(t => t.kv.Value.DcSlots - t.kv.Value.DcKids.Count).ThenBy(t => t.i).Select(t => t.kv).ToList();
        if (dcs.Count > 0) { Place(dcs[0].Key, dcs[0].Value, true); return; }
        n.Mode = "seg"; n.Depth = 1; Out(sid, new JsonObject { ["t"] = "mode", ["stream"] = st.Id, ["mode"] = "seg" }); if (!n.Member) SendSegs(sid, st); Report(st);
    }
    void Leave(Stream st, string sid)
    {
        if (!st.Nodes.TryGetValue(sid, out var n)) return;
        if (sid == st.Src)
        {   // the broadcaster left: the stream is over
            foreach (var id in st.Nodes.Keys) if (id != sid) { Out(id, new JsonObject { ["t"] = "ended", ["stream"] = st.Id }); if (sockets.TryGetValue(id, out var so)) so.Watching.Remove(st.Id); }
            streams.Remove(st.Id); return;
        }
        Detach(st, sid);
        var orphans = KidsOf(n);
        st.Nodes.Remove(sid); if (sockets.TryGetValue(sid, out var s)) s.Watching.Remove(st.Id);
        foreach (var c in orphans) if (st.Nodes.TryGetValue(c, out var x)) { x.Parent = null; Out(c, new JsonObject { ["t"] = "unparent", ["stream"] = st.Id, ["peer"] = sid }); Assign(st, c); }
        Report(st);
    }
    void RelaySeg(Stream st, JsonObject g)
    {
        g["at"] = Now();
        if (Num(g["n"]) == 0) st.Seg0 = g;
        st.Segs.Add(g);
        while (st.Segs.Count > 0 && Num(st.Segs[0]["at"]) < Num(g["at"]) - KeepMs) st.Segs.RemoveAt(0);
        foreach (var (id, n) in st.Nodes) if (id != st.Src && (n.Mode == "seg" || (n.Mode == "pending" && n.DcSlots > 0) || n.Member)) Out(id, new JsonObject { ["t"] = "seg", ["seg"] = g.DeepClone() });
    }

    /// <summary>A stream this computer produces itself (OBS through this node): its segments and chunks go to viewers.</summary>
    public (Action<JsonObject> Seg, Action End, Action<JsonObject> Chunk) Local(string stream, string chain, string mime)
    {
        lock (gate)
        {
            var src = "local:" + stream;
            var st = new Stream { Id = stream, Chain = chain, Src = src, Mime = mime, Media = false, Started = Now() };
            st.Nodes[src] = new Node { Member = true, Mode = "src" };
            streams[stream] = st;
            return (g => { lock (gate) if (streams.GetValueOrDefault(stream) == st) RelaySeg(st, g); },
                () => { lock (gate) if (streams.GetValueOrDefault(stream) == st) Leave(st, src); },
                c => { lock (gate) { if (streams.GetValueOrDefault(stream) != st) return; (st.Chunks ??= []).Add(c); foreach (var id in st.Nodes.Keys) if (id != src) Out(id, new JsonObject { ["t"] = "lchunk", ["stream"] = st.Id, ["chunk"] = c.DeepClone() }); } });
        }
    }
    public void Attach(string sid, Action<string> send)
    {
        lock (gate) { var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)); sockets[sid] = new Sock { Send = send, Nonce = nonce }; Out(sid, new JsonObject { ["t"] = "hi", ["nonce"] = nonce }); }
    }
    public void Detach(string sid)
    {
        lock (gate) { if (!sockets.TryGetValue(sid, out var s)) return; foreach (var id in s.Watching.ToList()) if (streams.TryGetValue(id, out var st)) Leave(st, sid); sockets.Remove(sid); }
    }
    public JsonObject Stats() { lock (gate) return new JsonObject { ["streams"] = streams.Count, ["sockets"] = sockets.Count }; }
    /// <summary>The streams here right now, with their viewer counts (for /status).</summary>
    public JsonArray Live()
    {
        lock (gate) return new JsonArray(streams.Values.Select(st => { var c = Counts(st); return (JsonNode?)new JsonObject { ["stream"] = st.Id, ["chain"] = st.Chain, ["viewers"] = c["viewers"]?.DeepClone(), ["now"] = c["now"]?.DeepClone(), ["seenCount"] = c["seenCount"]?.DeepClone() }; }).ToArray());
    }

    /// <summary>One message from a socket (each socket's messages are handled in order).</summary>
    public void Message(string sid, JsonNode? node)
    {
        lock (gate)
        {
            if (!sockets.TryGetValue(sid, out var s) || node is not JsonObject m || Str(Get(m, "t")) is not { } t) return;
            if (t == "auth")
            {   // prove you hold a key (your personal key, or a channel key to broadcast)
                if (IsKey(Get(m, "key")) && IsString(Get(m, "sig")) && s.Keys.Count < 50 && Rules.VerifySig(m["key"], "opentube-live-auth:" + s.Nonce, m["sig"])) s.Keys.Add(Str(m["key"])!);
                return;
            }
            if (t == "seg" && !m.ContainsKey("stream")) m["stream"] = Get(Get(m, "seg"), "stream")?.DeepClone();
            var stream = IsHex(Get(m, "stream"), 64) ? Str(m["stream"]) : null;
            streams.TryGetValue(stream ?? "", out var st);
            if (t == "broadcast")
            {
                if (stream == null || !IsHex(Get(m, "chain"), 64) || !IsStr(Get(m, "mime"), 80)) return;
                var chain = Str(m["chain"])!; var owner = OwnerOf(chain); var relay = SignerOf(chain, stream);
                if (!(owner != null && s.Keys.Contains(owner)) && !(relay != null && s.Keys.Contains(relay))) { Out(sid, new JsonObject { ["t"] = "error", ["stream"] = stream, ["error"] = "not the owner of this channel" }); return; }
                if (st != null && st.Chain != chain) return;
                if (st != null && st.Src != sid) Leave(st, st.Src);   // a reconnecting broadcaster replaces the old connection
                var rtcOff = Get(m, "rtc") is JsonValue rv && rv.GetValueKind() == System.Text.Json.JsonValueKind.False;
                var fresh = new Stream { Id = stream, Chain = chain, Src = sid, Started = Now(), Mime = Str(m["mime"])!, Media = !rtcOff, Seg0 = st?.Seg0, Segs = st?.Segs ?? [], Chunks = st?.Chunks ?? [], SeenBy = st?.SeenBy ?? [], Peak = st?.Peak ?? 0 };
                var slots = Clamp(m, "slots", MaxSlots);
                fresh.Nodes[sid] = new Node { Slots = rtcOff ? 0 : slots > 0 ? slots : SrcSlots, DcSlots = Clamp(m, "dcSlots", MaxDcSlots), Member = true, Mode = "src", Media = !rtcOff };
                streams[stream] = fresh; s.Watching.Add(stream);
                Out(sid, new JsonObject { ["t"] = "broadcasting", ["stream"] = stream }); return;
            }
            if (t == "ingest")
            {   // the owner asks for a stream key so OBS can send this stream to the node
                if (IngestTicket == null) { Out(sid, new JsonObject { ["t"] = "error", ["stream"] = Get(m, "stream")?.DeepClone(), ["error"] = "This hub can’t receive OBS streams (only an OpenTube computer can)." }); return; }
                if (stream == null || !IsHex(Get(m, "chain"), 64) || !IsStr(Get(m, "mime"), 80) || streams.ContainsKey(stream)) return;
                var owner = OwnerOf(Str(m["chain"])!);
                if (owner == null || !s.Keys.Contains(owner)) { Out(sid, new JsonObject { ["t"] = "error", ["stream"] = stream, ["error"] = "not the owner of this channel" }); return; }
                Out(sid, new JsonObject { ["t"] = "ingestkey", ["stream"] = stream, ["key"] = IngestTicket(new JsonObject { ["stream"] = stream, ["chain"] = m["chain"]!.DeepClone(), ["mime"] = m["mime"]!.DeepClone() }), ["path"] = "/ingest/" }); return;
            }
            if (st == null) { if (t is "watch" or "probe") Out(sid, new JsonObject { ["t"] = "nostream", ["stream"] = Get(m, "stream")?.DeepClone() }); return; }
            switch (t)
            {
                case "probe":
                {
                    var last = st.Segs.Count > 0 ? Num(st.Segs[^1]["at"])!.Value : st.Started;
                    Out(sid, Now() - last < 60000 ? new JsonObject { ["t"] = "up", ["stream"] = st.Id, ["at"] = last } : new JsonObject { ["t"] = "nostream", ["stream"] = st.Id, ["stale"] = true }); return;
                }
                case "lchunk":
                {   // from the streamer or a relaying member: store it and tell everyone watching
                    var c = Get(m, "chunk") as JsonObject;
                    if (!st.Nodes.TryGetValue(sid, out var n) || !(sid == st.Src || n.Member) || !ChunkShape(c) || Str(c!["stream"]) != st.Id || c.ToJsonString().Length > 100000) return;
                    st.Chunks ??= [];
                    if (st.Chunks.Any(x => Num(x["from"]) == Num(c["from"]) && Num(x["to"]) == Num(c["to"])) || st.Chunks.Count >= 3000) return;
                    var pre = $"opentube-live-chunk:{Str(c["stream"])}:{NumberToString(Num(c["from"])!.Value)}:{NumberToString(Num(c["to"])!.Value)}:{NumberToString(Num(c["o"])!.Value)}:{NumberToString(Num(c["len"])!.Value)}:{Str(c["ih"])}";
                    if (!Rules.VerifySig(c["by"], pre, c["sig"]) || !s.Keys.Contains(Str(c["by"])!)) return;
                    st.Chunks.Add(c);
                    foreach (var id in st.Nodes.Keys) if (id != sid) Out(id, new JsonObject { ["t"] = "lchunk", ["stream"] = st.Id, ["chunk"] = c.DeepClone() });
                    return;
                }
                case "seg":
                {
                    var g = Get(m, "seg") as JsonObject;
                    if (sid != st.Src || g == null || Str(Get(g, "stream")) != st.Id || !IsSafeInteger(Get(g, "n")) || !IsSafeInteger(Get(g, "o")) || Str(Get(g, "d")) is not { } d || d.Length > MaxSeg * 1.4) return;
                    RelaySeg(st, (JsonObject)g.DeepClone()); return;
                }
                case "watch":
                {
                    if (st.Nodes.ContainsKey(sid) || s.Watching.Count >= 20) return;
                    var n = new Node { Slots = Clamp(m, "slots", MaxSlots), DcSlots = Clamp(m, "dcSlots", MaxDcSlots), Member = MemberOf(sid, st.Chain), Media = !(Get(m, "media") is JsonValue mv && mv.GetValueKind() == System.Text.Json.JsonValueKind.False), Relay = IsTrue(Get(m, "relay")) };
                    st.Nodes[sid] = n; s.Watching.Add(st.Id);
                    if (!IsTrue(Get(m, "relay")) && !Truthy(Get(m, "anon")))
                    {
                        n.Who = s.Keys.FirstOrDefault();
                        if (n.Who != null) { var x = st.SeenBy.TryGetValue(n.Who, out var sx) ? sx : new Seen { First = Now(), Last = Now() }; x.Last = Now(); st.SeenBy[n.Who] = x; }
                    }
                    Out(sid, new JsonObject { ["t"] = "watching", ["stream"] = st.Id, ["mime"] = st.Mime, ["member"] = n.Member });
                    foreach (var c in (st.Chunks ?? []).TakeLast(600)) Out(sid, new JsonObject { ["t"] = "lchunk", ["stream"] = st.Id, ["chunk"] = c.DeepClone() });
                    if (n.Member) SendSegs(sid, st);
                    if (Truthy(Get(m, "segOnly"))) { n.Mode = "seg"; n.Depth = 1; Out(sid, new JsonObject { ["t"] = "mode", ["stream"] = st.Id, ["mode"] = "seg" }); if (!n.Member) SendSegs(sid, st); }
                    else Assign(st, sid);
                    if (n.DcSlots >= 4 && n.Mode is "seg" or "dc")
                        foreach (var (id, x) in st.Nodes.ToList()) if (n.DcKids.Count < n.DcSlots && id != sid && x.Mode == "seg" && !x.Member && x.DcKids.Count == 0 && x.DcSlots >= 0) { x.Mode = "pending"; Assign(st, id); }
                    Report(st); return;
                }
                case "alive":
                {
                    if (!st.Nodes.TryGetValue(sid, out var n)) return;
                    if (n.Who != null && st.SeenBy.TryGetValue(n.Who, out var x)) { x.Secs += Math.Min(60, (Now() - x.Last) / 1000); x.Last = Now(); }
                    n.Seen = Now();
                    if (Get(m, "q") is JsonObject q)
                    {
                        double? kb = IsFinite(Get(q, "kbps")) ? Math.Max(0, Math.Min(1e6, Ledger.JsRound(Num(q["kbps"])!.Value))) : null;
                        double? buf = IsFinite(Get(q, "buf")) ? Math.Max(0, Math.Min(120, Math.Round(Num(q["buf"])!.Value, 1, MidpointRounding.AwayFromZero))) : null;
                        var stalls = Num(Get(q, "stalls")) is double sv && double.IsFinite(sv) ? Math.Max(0, Math.Min(100, (int)sv)) : 0;
                        n.Q = new JsonObject { ["kbps"] = kb, ["stalls"] = stalls, ["buf"] = buf }; n.QAt = Now();
                    }
                    return;
                }
                case "leave": Leave(st, sid); return;
                case "fallback":
                {   // WebRTC didn't work for this viewer: segments instead
                    if (!st.Nodes.TryGetValue(sid, out var n) || n.Mode == "src") return;
                    Detach(st, sid);
                    foreach (var c in KidsOf(n)) if (st.Nodes.TryGetValue(c, out var x)) { x.Parent = null; Out(c, new JsonObject { ["t"] = "unparent", ["stream"] = st.Id, ["peer"] = sid }); Assign(st, c); }
                    n.Mode = "seg"; n.Depth = 1; Out(sid, new JsonObject { ["t"] = "mode", ["stream"] = st.Id, ["mode"] = "seg" }); if (!n.Member) SendSegs(sid, st);
                    Report(st); return;
                }
                case "signal":
                {   // WebRTC offer/answer/candidates, only between a parent and its child
                    var to = Str(Get(m, "to"));
                    if (to != null && st.Nodes.TryGetValue(sid, out var a) && st.Nodes.TryGetValue(to, out var b) && (a.Parent == to || b.Parent == sid) && (Get(m, "data")?.ToJsonString() ?? "null").Length < 20000)
                        Out(to, new JsonObject { ["t"] = "signal", ["stream"] = st.Id, ["from"] = sid, ["data"] = Get(m, "data")?.DeepClone() });
                    return;
                }
            }
        }
    }
    static double Clamp(JsonObject m, string k, double max) { var v = Num(Get(m, k)) is double d && double.IsFinite(d) ? (int)d : 0; return Math.Min(Math.Max(v, 0), max); }   // (m.k | 0) clamped
    static bool ChunkShape(JsonObject? c) => c != null && IsHex(Get(c, "stream"), 64) && IsSafeInteger(Get(c, "from")) && IsSafeInteger(Get(c, "to")) && Num(c["to"]) >= Num(c["from"]) && Num(c["to"]) - Num(c["from"]) < 200
        && IsSafeInteger(Get(c, "o")) && IsSafeInteger(Get(c, "len")) && Num(c["len"]) > 0 && Num(c["len"]) < 64 * 1024 * 1024 && IsHex(Get(c, "ih"), 40) && IsKey(Get(c, "by")) && IsString(Get(c, "sig"))
        && Get(c, "segs") is JsonArray segs && segs.Count == Num(c["to"]) - Num(c["from"]) + 1 && segs.All(g => IsSafeInteger(Get(g, "n")) && IsSafeInteger(Get(g, "o")) && IsHex(Get(g, "h"), 64) && IsString(Get(g, "sig")));
}
