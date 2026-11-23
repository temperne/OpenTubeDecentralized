using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// Other ways OBS reaches this computer (as the JavaScript node):
///  • /ingest/&lt;key&gt;: OBS (Recording → Custom Output (FFmpeg) → Output to URL, WebM) posts the stream here; the owner's
///    browser got the key from this computer's live hub and named this computer as the stream's relay, so the bytes
///    are cut into ~1 s chunks, signed with this computer's key and fed to its hub like any broadcaster's segments;
///  • /rtmp: RTMP inside a WebSocket, to this computer's own RTMP port, so OBS reaches a computer behind a Cloudflare
///    Tunnel (web traffic only) through the bridge "opentube --obs-via=https://…" on the OBS computer.
/// </summary>
public static class Ingest
{
    sealed record Ticket(string Stream, string Chain, string Mime, double Exp);
    static readonly ConcurrentDictionary<string, Ticket> tickets = new();
    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static string NewTicket(JsonObject t)
    {
        var key = B64u.Encode(RandomNumberGenerator.GetBytes(18));
        tickets[key] = new Ticket((string)t["stream"]!, (string)t["chain"]!, (string)t["mime"]!, Now() + 12 * 3600000);
        return key;
    }
    public static async Task Post(HttpContext ctx, OpenTubeNode node, string key)
    {
        if (!tickets.TryRemove(key, out var t) || t.Exp < Now()) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("unknown or expired stream key"); return; }   // one stream per key
        var src = node.Hub.Local(t.Stream, t.Chain, t.Mime);
        node.Log($"live: OBS stream started ({t.Stream[..8]}…)");
        long n = 0, o = 0; var parts = new MemoryStream(); var last = DateTime.UtcNow; var gate = new object();
        void Flush()
        {
            lock (gate)
            {
                if (parts.Length == 0) return;
                var bytes = parts.ToArray(); parts.SetLength(0);
                var h = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var g = new JsonObject { ["stream"] = t.Stream, ["n"] = n, ["o"] = o, ["h"] = h };
                g["sig"] = node.Identity!.Sign($"opentube-live:{t.Stream}:{n}:{o}:{h}"); g["d"] = Convert.ToBase64String(bytes);
                n++; o += bytes.Length; src.Seg(g);
            }
        }
        using var tick = new Timer(_ => { if (DateTime.UtcNow - last >= TimeSpan.FromSeconds(1)) { last = DateTime.UtcNow; Flush(); } }, null, 250, 250);
        var buf = new byte[65536];
        try
        {
            while (true)
            {
                var r = await ctx.Request.Body.ReadAsync(buf); if (r <= 0) break;
                lock (gate) parts.Write(buf, 0, r);
                if (parts.Length >= 512 * 1024) { last = DateTime.UtcNow; Flush(); }
            }
        }
        catch { }
        Flush();
        _ = Task.Delay(2000).ContinueWith(_ => { src.End(); node.Log($"live: OBS stream ended ({o / 1e6:0.0} MB)"); });
        await ctx.Response.WriteAsync("ok");
    }

    /// <summary>/rtmp: a WebSocket carrying RTMP to this computer's own RTMP port.</summary>
    public static async Task RtmpOverWs(WebSocket ws, int rtmpPort)
    {
        using var tcp = new TcpClient { NoDelay = true };
        try { await tcp.ConnectAsync("127.0.0.1", rtmpPort); } catch { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "OBS receiving is off here", default); return; }
        var s = tcp.GetStream(); using var cts = new CancellationTokenSource();
        var down = Task.Run(async () => { var b = new byte[65536]; try { while (true) { var r = await s.ReadAsync(b, cts.Token); if (r <= 0) break; await ws.SendAsync(b.AsMemory(0, r), WebSocketMessageType.Binary, true, cts.Token); } } catch { } cts.Cancel(); });
        var buf = new byte[65536];
        try { while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested) { var r = await ws.ReceiveAsync(buf, cts.Token); if (r.MessageType == WebSocketMessageType.Close) break; await s.WriteAsync(buf.AsMemory(0, r.Count), cts.Token); } } catch { }
        cts.Cancel(); try { await down; } catch { }
    }

    /// <summary>opentube --obs-via=https://…: a bridge on the OBS computer: RTMP on 127.0.0.1 (port 1935 or --obs-port), carried in a WebSocket to that computer's /rtmp.</summary>
    public static async Task<int> Bridge(string via, int port)
    {
        Uri to;
        try { var u = new UriBuilder(via) { Path = new Uri(via).AbsolutePath.TrimEnd('/') + "/rtmp", Query = "" }; u.Scheme = u.Scheme == "http" ? "ws" : "wss"; u.Port = u.Uri.IsDefaultPort ? -1 : u.Port; to = u.Uri; }
        catch { Console.Error.WriteLine("--obs-via needs the computer’s address, like --obs-via=https://example.trycloudflare.com"); return 1; }
        var l = new TcpListener(System.Net.IPAddress.Loopback, port);
        try { l.Start(); } catch { Console.Error.WriteLine($"Port {port} is in use here (an OpenTube receiving OBS?). Use --obs-port=1936 and put rtmp://127.0.0.1:1936/live in OBS."); return 1; }
        Console.WriteLine($"OBS bridge ready. In OBS: Settings → Stream → Custom…, Server rtmp://127.0.0.1{(port == 1935 ? "" : ":" + port)}/live and your stream key from that computer. Forwarding to {to.Host}. Ctrl+C stops.");
        while (true)
        {
            var c = await l.AcceptTcpClientAsync(); c.NoDelay = true;
            _ = Task.Run(async () =>
            {
                using var _c = c; using var ws = new ClientWebSocket();
                try { await ws.ConnectAsync(to, default); } catch { Console.WriteLine($"[obs-via] couldn’t reach {to} (is OBS receiving switched on there, and is it updated?)"); return; }
                Console.WriteLine($"[obs-via] OBS connected: forwarding to {to.Host}");
                var s = c.GetStream(); using var cts = new CancellationTokenSource();
                var up = Task.Run(async () => { var b = new byte[65536]; try { while (true) { var r = await s.ReadAsync(b, cts.Token); if (r <= 0) break; await ws.SendAsync(b.AsMemory(0, r), WebSocketMessageType.Binary, true, cts.Token); } } catch { } cts.Cancel(); });
                var buf = new byte[65536];
                try { while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested) { var r = await ws.ReceiveAsync(buf, cts.Token); if (r.MessageType == WebSocketMessageType.Close) break; await s.WriteAsync(buf.AsMemory(0, r.Count), cts.Token); } } catch { }
                cts.Cancel(); try { await up; } catch { }
            });
        }
    }
}
