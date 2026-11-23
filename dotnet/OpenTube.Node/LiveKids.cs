using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SIPSorcery.Net;

namespace OpenTube.Node;

/// <summary>
/// The segment tree from this computer (as the JavaScript node's liveKids): the hub names viewers to be our children;
/// we offer each a WebRTC data channel "live" (signalled through the hub) and send it the start of the stream, the
/// last few segments, then every new one. Viewers pass them on to their own children. Messages are kept under 64 KB
/// (bigger ones go in numbered parts, as the core's dcSend), and a child too far behind is skipped (it has the hub).
/// </summary>
public sealed class LiveKids(string stream, Action<JsonObject> sendHub, Action<string> log)
{
    sealed class Kid { public required RTCPeerConnection Pc; public RTCDataChannel? Ch; public readonly List<RTCIceCandidateInit> Waiting = []; public bool Open; }
    readonly ConcurrentDictionary<string, Kid> kids = [];
    JsonObject? seg0; readonly List<JsonObject> recent = [];
    public int Count => kids.Count;
    public int OpenCount => kids.Values.Count(k => k.Open);
    static readonly RTCConfiguration Config = new() { iceServers = [new RTCIceServer { urls = "stun:stun.l.google.com:19302" }] };
    const int Part = 60000;

    public void Child(string peer)
    {
        Unchild(peer);
        var pc = new RTCPeerConnection(Config); var kid = new Kid { Pc = pc }; kids[peer] = kid;
        pc.onicecandidate += c =>
        {
            if (c == null || string.IsNullOrEmpty(c.candidate)) return;
            var cand = c.candidate.StartsWith("candidate:") ? c.candidate : "candidate:" + c.candidate;
            sendHub(new JsonObject { ["t"] = "signal", ["stream"] = stream, ["to"] = peer, ["data"] = new JsonObject { ["c"] = new JsonObject { ["candidate"] = cand, ["sdpMid"] = c.sdpMid ?? "0", ["sdpMLineIndex"] = c.sdpMLineIndex } } });
        };
        pc.onconnectionstatechange += st => { if (st is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed && kids.TryGetValue(peer, out var k) && k == kid) kids.TryRemove(peer, out _); };
        _ = Task.Run(async () =>
        {
            try
            {
                var ch = await pc.createDataChannel("live", new RTCDataChannelInit { ordered = true }); kid.Ch = ch;
                ch.onopen += () =>
                {
                    kid.Open = true;
                    log($"[live] a viewer gets the stream from this computer over WebRTC ({OpenCount} now)");
                    JsonObject? first; List<JsonObject> last; lock (recent) { first = seg0; last = [.. recent]; }
                    if (first != null) Send(kid, first);
                    foreach (var g in last) if (Js(g) != 0) Send(kid, g);
                };
                ch.onclose += () => kid.Open = false;
                var offer = pc.createOffer(null);
                await pc.setLocalDescription(offer);
                sendHub(new JsonObject { ["t"] = "signal", ["stream"] = stream, ["to"] = peer, ["data"] = new JsonObject { ["sdp"] = new JsonObject { ["type"] = "offer", ["sdp"] = offer.sdp } } });
            }
            catch (Exception e) { log("[live] offer failed: " + e.Message); }
        });
    }
    public void Unchild(string peer) { if (kids.TryRemove(peer, out var k)) try { k.Pc.close(); } catch { } }
    public void Signal(string peer, JsonNode? data)
    {
        if (!kids.TryGetValue(peer, out var k) || data is not JsonObject d) return;
        try
        {
            if (d["sdp"] is JsonObject sdp && (string?)sdp["sdp"] is { } text)
            {
                var r = k.Pc.setRemoteDescription(new RTCSessionDescriptionInit { type = (string?)sdp["type"] == "answer" ? RTCSdpType.answer : RTCSdpType.offer, sdp = text });
                if (r != SetDescriptionResultEnum.OK) { log("[live] the viewer's answer didn't fit: " + r); return; }
                List<RTCIceCandidateInit> w; lock (k.Waiting) { w = [.. k.Waiting]; k.Waiting.Clear(); }
                foreach (var c in w) k.Pc.addIceCandidate(c);
            }
            else if (d["c"] is JsonObject c && (string?)c["candidate"] is { Length: > 0 } cand)
            {
                var ic = new RTCIceCandidateInit { candidate = cand, sdpMid = (string?)c["sdpMid"] ?? "0", sdpMLineIndex = (ushort)(OpenTube.Core.Js.Num(c["sdpMLineIndex"]) ?? 0) };
                if (k.Pc.remoteDescription != null) k.Pc.addIceCandidate(ic); else lock (k.Waiting) k.Waiting.Add(ic);
            }
        }
        catch (Exception e) { log("[live] signal: " + e.Message); }
    }
    public void Seg(JsonObject g)
    {
        lock (recent) { if (Js(g) == 0) seg0 = g; recent.Add(g); if (recent.Count > 8) recent.RemoveAt(0); }
        foreach (var k in kids.Values) Send(k, g);
    }
    public void Close() { foreach (var k in kids.Values) try { k.Pc.close(); } catch { } kids.Clear(); }

    static long Js(JsonObject g) => (long)(OpenTube.Core.Js.Num(g["n"]) ?? -1);
    /// <summary>The core's dcSend: one message, or numbered parts under 64 KB; skipped when the child is far behind.</summary>
    static void Send(Kid k, JsonObject obj)
    {
        var ch = k.Ch; if (ch == null || !k.Open || ch.readyState != RTCDataChannelState.open || ch.bufferedAmount > 8 * 1024 * 1024) return;
        var s = obj.ToJsonString();
        try
        {
            if (s.Length <= Part) { ch.send(s); return; }
            var id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)); int n = (s.Length + Part - 1) / Part;
            for (int i = 0; i < n; i++) ch.send(new JsonObject { ["f"] = id, ["i"] = i, ["n"] = n, ["d"] = s.Substring(i * Part, Math.Min(Part, s.Length - i * Part)) }.ToJsonString());
        }
        catch { }
    }
}
