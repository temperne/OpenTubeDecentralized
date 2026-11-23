using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// The group stream (as the JavaScript node): everyone streaming into one person's group (their own OBS keys, guests
/// they invited, browser streams sent into it) combined into one 1280×720 picture, laid out by how many are on air,
/// audio mixed. Nothing restarts when people join or leave: each input has its own decoder (ffmpeg → raw frames at its
/// tile size, raw audio), this computer lays the picture out 30 times a second from each input's latest frame, and one
/// encoder runs for the whole stream, fed at a steady pace. The host's studio: who's on air (a waiting room with
/// previews), the layout (grid, spotlight, solo), a scrolling ticker and a logo (the channel's avatar by default).
/// (Audio goes between ffmpeg and this program over loopback TCP: .NET has no pipe 3.)
/// </summary>
public sealed class GroupStudio(OpenTubeNode node, ObsLive obs, string hostUser)
{
    const int Max = 9, GW = 1280, GH = 720, FPS = 30, AFRAME = 48000 / FPS * 4, PLAY_MAX = FPS * 4, THUMB_W = 320, THUMB_H = 180, TICKER_H = 48, LOGO_W = 160, LOGO_H = 90;
    public sealed class Input
    {
        public required string Id, Label, User, Kind, Fmt; public double At;
        public byte[]? Init; public readonly List<byte[]> Recent = [];
        public Process? Dec; public int DecW, DecH, DecFails, Version;
        public byte[]? Last; public int LastW, LastH;
        public readonly Queue<byte[]> Vq = new(); public readonly AudioBuf Aq = new();
        public bool Playing, Ended, OnAir, Muted; public int Target;
        public Action? Close; public long InB;
        public System.Threading.Channels.Channel<byte[]>? DecIn; public long DecBacklog;
    }
    /// <summary>Raw audio (s16le stereo) waiting to be played out.</summary>
    public sealed class AudioBuf
    {
        byte[] buf = new byte[1 << 16]; int start, len;
        public int Length { get { lock (this) return len; } }
        public void Add(ReadOnlySpan<byte> d) { lock (this) { if (start + len + d.Length > buf.Length) { var n = new byte[Math.Max(buf.Length, (len + d.Length) * 2)]; Array.Copy(buf, start, n, 0, len); buf = n; start = 0; } d.CopyTo(buf.AsSpan(start + len)); len += d.Length; } }
        public byte[] Take(int n) { lock (this) { n = Math.Min(n, len) & ~3; var r = buf.AsSpan(start, n).ToArray(); start += n; len -= n; if (len == 0) start = 0; return r; } }
        public void Drop(int n) { lock (this) { n = Math.Min(n, len); start += n; len -= n; if (len == 0) start = 0; } }
        public void Clear() { lock (this) { start = 0; len = 0; } }
        public void KeepLast(int n) { lock (this) { if (len > n) { start += len - n; len = n; } } }
    }
    record Tile(int X, int Y, int W, int H);

    readonly Dictionary<string, Input> inputs = [];
    readonly object gate = new();
    Process? enc; ObsLive.Sink? output; bool stopping; int encFails; string layoutKey = "";
    List<Input> list = [], all = []; List<Tile> tiles = [];
    (byte[] Buf, int W)? ticker; byte[]? logo;
    readonly HashSet<string> absorbed = []; bool pointing;
    Timer? startTimer; CancellationTokenSource? clockCts;
    void Log(string m) => node.Log("[group] " + m);
    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    JsonObject Profile => obs.ProfileOf(hostUser);
    public bool On => Js.IsTrue(Profile["tile"]?["on"]);
    public bool Live => output?.Block != null;
    public bool Starting => output != null && output.Block == null;

    // ---- the host's studio settings (kept with their OBS profile, so they stay for the next stream)
    JsonObject Studio()
    {
        var p = Profile; if (p["studio"] is not JsonObject s) { s = []; p["studio"] = s; }
        if ((string?)s["layout"] is not ("grid" or "spotlight" or "solo")) s["layout"] = "grid";
        if (!s.ContainsKey("spot")) s["spot"] = null; if (s["waiting"] == null) s["waiting"] = true;
        if (s["ticker"] is not JsonObject t) { t = []; s["ticker"] = t; } t["on"] ??= false; t["text"] ??= ""; t["speed"] ??= 4;
        if (s["logo"] is not JsonObject l) { l = []; s["logo"] = l; } l["on"] ??= true; l["pos"] ??= "tr"; if (!l.ContainsKey("file")) l["file"] = null;
        return s;
    }

    /// <summary>One input (an OBS connection, or a browser stream this node pulls): the sink its data goes to, or null when the picture is full.</summary>
    public IStreamSink? AddInput(string id, string label, string user, string kind, string fmt, Action? close)
    {
        lock (gate)
        {
            if (inputs.TryGetValue(id, out var old) && !old.Ended) { old.Ended = true; Kill(old); try { old.Close?.Invoke(); } catch { } }   // reconnecting replaces itself
            else if (inputs.Values.Count(x => !x.Ended) >= Max) return null;
            var x = new Input { Id = id, Label = label, User = user, Kind = kind, Fmt = fmt, At = Now(), Close = close, OnAir = user == hostUser || !Js.IsTrue(Studio()["waiting"]) };
            inputs[id] = x;
            return new InputSink(this, x);
        }
    }
    sealed class InputSink(GroupStudio g, Input x) : IStreamSink
    {
        public int FragMs => 250;   // OBS: send its video on in ¼ s pieces, so the playout buffer stays short
        public void Init(byte[] header, string mime)
        {
            lock (g.gate)
            {
                x.Init = header; x.Recent.Clear();
                if (g.enc != null) { g.Relayout($"{x.Label} joined"); return; }
                g.startTimer ??= new Timer(_ => g.Start(), null, 1500, Timeout.Infinite);   // a moment for others connecting at the same time
            }
        }
        public void Fragment(byte[] b, bool key) => g.Write(x, b);
        public void End() => g.EndInput(x);
    }
    void Write(Input x, byte[] b)
    {
        x.InB += b.Length;
        lock (gate) { x.Recent.Add(b); if (x.Recent.Count > 4) x.Recent.RemoveAt(0); }
        if (x.DecIn is { } ch)
        {
            Interlocked.Add(ref x.DecBacklog, b.Length); ch.Writer.TryWrite(b);
            if (Interlocked.Read(ref x.DecBacklog) > 24_000_000) { Log($"{x.Label}: this computer can’t decode their video fast enough; skipping ahead"); lock (gate) DecStart(x, x.DecW, x.DecH); }
        }
    }
    void EndInput(Input x)
    {
        lock (gate)
        {
            if (x.Ended) return; x.Ended = true; Kill(x);
            inputs.Remove(x.Id);
            if (!inputs.Values.Any(y => !y.Ended)) { startTimer?.Dispose(); startTimer = null; Stop(); return; }   // the encoder finishes the stream
            Relayout($"{x.Label} left");
        }
    }
    static void Kill(Input x) { try { x.Dec?.Kill(true); } catch { } x.Dec = null; x.DecIn?.Writer.TryComplete(); x.DecIn = null; }

    // ---- one input's decoder, at its tile size: raw frames out (queued), raw audio out (over loopback TCP)
    void DecStart(Input x, int cw, int ch)
    {
        Kill(x); if (x.Init == null) return;
        var version = ++x.Version; var size = cw * ch * 3 / 2;
        var audio = new TcpListener(IPAddress.Loopback, 0); audio.Start(); var aport = ((IPEndPoint)audio.LocalEndpoint).Port;
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-probesize", "2000000", "-analyzeduration", "2000000", "-fflags", "+nobuffer+genpts+discardcorrupt",
            "-f", x.Fmt == "webm" ? "matroska" : "mp4", "-i", "pipe:0",
            "-map", "0:v:0", "-vf", $"fps={FPS},scale={cw}:{ch}:force_original_aspect_ratio=decrease,pad={cw}:{ch}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p", "-f", "rawvideo", "pipe:1",
            "-map", "0:a:0?", "-f", "s16le", "-ar", "48000", "-ac", "2", $"tcp://127.0.0.1:{aport}" }) psi.ArgumentList.Add(a);
        Process dec;
        try { dec = Process.Start(psi)!; } catch (Exception e) { audio.Stop(); Log("can’t start ffmpeg: " + e.Message); return; }
        x.Dec = dec; x.DecW = cw; x.DecH = ch;
        // a restarted decoder first replays the latest few seconds (to reach a keyframe): that burst is skipped, the
        // picture holds its last frame meanwhile; it's over once the decoder goes quiet for a moment
        var replay = x.Recent.Count > 0; var quiet = new Timer(_ => replay = false, null, Timeout.Infinite, Timeout.Infinite);
        void Replayed() => quiet.Change(250, Timeout.Infinite);
        if (replay) { lock (x.Vq) x.Vq.Clear(); x.Aq.Clear(); x.Playing = false; Replayed(); }
        var err = new StringBuilder();
        dec.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) { err.Append(e.Data).Append('\n'); if (err.Length > 400) err.Remove(0, err.Length - 400); } };
        dec.BeginErrorReadLine();
        _ = Task.Run(async () =>
        {   // video frames
            var s = dec.StandardOutput.BaseStream; var buf = new byte[size];
            try
            {
                while (true)
                {
                    await s.ReadExactlyAsync(buf);
                    if (replay) { Replayed(); continue; }
                    var f = (byte[])buf.Clone();
                    lock (x.Vq)
                    {
                        x.Vq.Enqueue(f);
                        if (x.Last == null) { x.Last = f; x.LastW = cw; x.LastH = ch; }   // a picture for the preview right away
                        var over = x.Vq.Count - PLAY_MAX; for (int i = 0; i < over; i++) { x.Vq.Dequeue(); x.Aq.Drop(AFRAME); }   // never more than the cap; sound stays with the picture
                    }
                }
            }
            catch { }
        });
        _ = Task.Run(async () =>
        {   // audio
            try
            {
                using var c = await audio.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(30)); audio.Stop();
                var s = c.GetStream(); var buf = new byte[16384];
                while (true)
                {
                    int n = await s.ReadAsync(buf); if (n <= 0) break;
                    if (replay) { Replayed(); continue; }
                    x.Aq.Add(buf.AsSpan(0, n));
                    if (x.Aq.Length > AFRAME * (PLAY_MAX + FPS)) x.Aq.KeepLast(AFRAME * PLAY_MAX);   // audio-only input: same cap
                }
            }
            catch { }
            finally { try { audio.Stop(); } catch { } }
        });
        var input = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(); x.DecIn = input; x.DecBacklog = 0;
        input.Writer.TryWrite(x.Init); foreach (var r in x.Recent) input.Writer.TryWrite(r);   // from its latest video: decodes from the next keyframe
        _ = Task.Run(async () =>
        {
            var w = dec.StandardInput.BaseStream;
            try { await foreach (var b in input.Reader.ReadAllAsync()) { await w.WriteAsync(b); await w.FlushAsync(); Interlocked.Add(ref x.DecBacklog, -b.Length); } } catch { }
            try { w.Close(); } catch { }
        });
        dec.EnableRaisingEvents = true;
        dec.Exited += (_, _) =>
        {
            quiet.Dispose();
            lock (gate)
            {
                if (x.Version != version || x.Ended) return;   // replaced or stopped on purpose
                x.Dec = null; x.DecFails++;
                string why; lock (err) why = err.ToString().Trim().Split('\n').LastOrDefault() ?? "";
                Log($"{x.Label}: the video decoder stopped (exit {Try(() => dec.ExitCode)}){(why.Length > 0 ? ": " + why[..Math.Min(200, why.Length)] : "")}; restarting it");
            }
            _ = Task.Delay(Math.Min(10000, 500 * x.DecFails)).ContinueWith(_ => { lock (gate) if (!x.Ended && x.Dec == null && x.Version == version) DecStart(x, cw, ch); });
        };
    }
    static T? Try<T>(Func<T> f) { try { return f(); } catch { return default; } }

    /// <summary>One output frame's worth from input x: advances its picture, returns its audio for this frame (or null).</summary>
    static byte[]? Playout(Input x)
    {
        if (x.Target == 0) x.Target = FPS / 2;
        int buffered; lock (x.Vq) buffered = Math.Max(x.Vq.Count, x.Aq.Length / AFRAME);
        if (!x.Playing) { if (buffered < x.Target) return null; x.Playing = true; }
        if (buffered == 0) { x.Playing = false; x.Target = Math.Min(FPS * 3, x.Target + FPS / 2); return null; }   // ran dry: wait for more
        lock (x.Vq) if (x.Vq.Count > 0) { x.Last = x.Vq.Dequeue(); x.LastW = x.DecW; x.LastH = x.DecH; }
        return x.Aq.Take(AFRAME);
    }

    static List<Tile> Grid(int k)
    {
        int cols = k <= 1 ? 1 : k <= 4 ? 2 : 3, rows = (k + cols - 1) / cols, cw = GW / cols / 2 * 2, ch = GH / rows / 2 * 2;
        return Enumerable.Range(0, k).Select(i => new Tile(i % cols * cw, i / cols * ch, cw, ch)).ToList();
    }
    static List<Tile> StudioTiles(string mode, int k)
    {
        if (k == 0) return [];
        if (mode == "solo" || k == 1) return [new Tile(0, 0, GW, GH)];
        if (mode == "spotlight") { int n = Math.Min(k - 1, 4), y0 = (GH - n * THUMB_H) / 4 * 2; return [new Tile(0, 90, 960, 540), .. Enumerable.Range(0, n).Select(i => new Tile(960, y0 + i * THUMB_H, THUMB_W, THUMB_H))]; }
        return Grid(k);
    }
    void Relayout(string? why = null)
    {
        var st = Studio(); var layout = (string)st["layout"]!;
        var everyone = inputs.Values.Where(x => !x.Ended && x.Init != null).OrderBy(x => x.At).ToList();
        var air = everyone.Where(x => x.OnAir).ToList();
        var spot = air.FirstOrDefault(x => x.Id == (string?)st["spot"]) ?? air.FirstOrDefault();
        if (layout == "solo") air = spot != null ? [spot] : [];
        else if (layout == "spotlight") air = spot != null ? new[] { spot }.Concat(air.Where(x => x != spot)).Take(5).ToList() : [];
        else air = air.Take(Max).ToList();
        var t = StudioTiles(layout, air.Count);
        var key = string.Join(",", air.Select((x, i) => $"{x.Id}@{t[i].X},{t[i].Y},{t[i].W}x{t[i].H}")) + "|" + string.Join(",", everyone.Select(x => x.Id));
        if (key == layoutKey) return; layoutKey = key; list = air; tiles = t; all = everyone;
        for (int i = 0; i < air.Count; i++) if (air[i].DecW != t[i].W || air[i].DecH != t[i].H || air[i].Dec == null) DecStart(air[i], t[i].W, t[i].H);
        foreach (var x in everyone) if (!air.Contains(x) && (x.DecW != THUMB_W || x.DecH != THUMB_H || x.Dec == null)) DecStart(x, THUMB_W, THUMB_H);   // off air: a preview for the host
        var waiting = everyone.Where(x => !x.OnAir).ToList();
        Log($"on air ({layout}): {(air.Count > 0 ? string.Join(", ", air.Select(x => x.Label)) : "nobody")}{(waiting.Count > 0 ? " · waiting: " + string.Join(", ", waiting.Select(x => x.Label)) : "")}{(why != null ? $" ({why})" : "")}");
    }

    // ---- the stream: one encoder for the session
    void Start()
    {
        lock (gate)
        {
            startTimer?.Dispose(); startTimer = null;
            if (enc != null) { Relayout(); return; }
            AbsorbHostLive();   // bring in the host's ongoing browser stream
            Relayout(); if (all.Count == 0) return;
            var st = Studio(); _ = BuildTicker((string?)st["ticker"]!["text"] ?? ""); _ = BuildLogo(LogoFile());
            var chain = obs.ChannelFor(hostUser, Profile);
            if (chain == null) { Log("no channel picked for the group stream (Devices → OBS → Channel)"); return; }
            var title = Profile["_absorbedTitle"] is JsonNode at ? (string?)at ?? "Live" : (string?)Profile["title"] ?? "Live"; Profile.Remove("_absorbedTitle");
            output = obs.NewSink(chain, title, Profile); encFails = 0; stopping = false;
            EncStart(true);
        }
    }
    void EncStart(bool first)
    {
        var o = output!;
        var audio = new TcpListener(IPAddress.Loopback, 0); audio.Start(); var aport = ((IPEndPoint)audio.LocalEndpoint).Port;
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error",
            "-thread_queue_size", "512", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", $"{GW}x{GH}", "-r", FPS.ToString(), "-i", "pipe:0",
            "-thread_queue_size", "512", "-probesize", "32", "-analyzeduration", "0", "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", $"tcp://127.0.0.1:{aport}",
            "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-preset", "superfast", "-tune", "zerolatency", "-profile:v", "high", "-level", "4.0",
            "-g", FPS.ToString(), "-keyint_min", FPS.ToString(), "-sc_threshold", "0", "-pix_fmt", "yuv420p",   // a keyframe (a fragment) every second
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-max_interleave_delta", "0", "-flush_packets", "1",
            "-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof", "pipe:1" }) psi.ArgumentList.Add(a);
        Process e;
        try { e = Process.Start(psi)!; } catch (Exception ex) { audio.Stop(); Log("can’t start ffmpeg: " + ex.Message); return; }
        enc = e; var frags = 0;
        e.ErrorDataReceived += (_, d) => { if (!string.IsNullOrWhiteSpace(d.Data)) Log(d.Data.Trim()[..Math.Min(200, d.Data.Trim().Length)]); };
        e.BeginErrorReadLine();
        _ = Task.Run(async () =>
        {   // fragmented MP4 out: the init (ftyp+moov), then a fragment (moof+mdat) a second, each starting with a keyframe
            var s = e.StandardOutput.BaseStream; var head = new byte[8]; var init = new MemoryStream(); MemoryStream? frag = null; bool inited = false;
            try
            {
                while (true)
                {
                    await s.ReadExactlyAsync(head); var size = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head); var type = Encoding.ASCII.GetString(head, 4, 4);
                    var body = new byte[size - 8]; await s.ReadExactlyAsync(body);
                    if (!inited) { init.Write(head); init.Write(body); if (type == "moov") { inited = true; if (first) o.Init(init.ToArray(), "video/mp4; codecs=\"avc1.640028,mp4a.40.2\""); else o.Reinit(init.ToArray()); } continue; }
                    if (type == "moof") { frag = new MemoryStream(); }
                    frag?.Write(head); frag?.Write(body);
                    if (type == "mdat" && frag != null) { frags++; o.Fragment(frag.ToArray(), true); frag = null; }
                }
            }
            catch { }
        });
        var video = System.Threading.Channels.Channel.CreateBounded<byte[]>(new System.Threading.Channels.BoundedChannelOptions(FPS * 4) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });
        var sound = System.Threading.Channels.Channel.CreateBounded<byte[]>(new System.Threading.Channels.BoundedChannelOptions(FPS * 4) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });
        _ = Task.Run(async () => { var w = e.StandardInput.BaseStream; try { await foreach (var f in video.Reader.ReadAllAsync()) { await w.WriteAsync(f); } } catch { } try { w.Close(); } catch { } });
        _ = Task.Run(async () =>
        {
            try { using var c = await audio.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(30)); audio.Stop(); var w = c.GetStream(); await foreach (var a in sound.Reader.ReadAllAsync()) await w.WriteAsync(a); }
            catch { } finally { try { audio.Stop(); } catch { } }
        });
        var cts = clockCts = new CancellationTokenSource();
        var t0 = Stopwatch.StartNew(); long sent = 0;
        var clock = new Thread(() =>
        {
            var frame = new byte[GW * GH * 3 / 2]; var mix = new int[AFRAME / 2]; long n = 0;
            while (!cts.IsCancellationRequested)
            {
                var due = t0.ElapsedMilliseconds * FPS / 1000 + 1;
                if (due - sent > FPS * 2) sent = due - 1;   // over 2 s behind (an overloaded computer): skip ahead instead of piling up
                while (sent < due && !cts.IsCancellationRequested)
                {
                    if (video.Reader.Count > (frags > 0 ? FPS / 2 : FPS * 3)) { sent = due; break; }   // the encoder is behind: skip, don't pile up memory
                    Compose(frame, mix, n++);
                    var ab = new byte[AFRAME]; for (int k = 0; k < mix.Length; k++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(ab.AsSpan(k * 2), (short)Math.Clamp(mix[k], -32768, 32767));
                    video.Writer.TryWrite((byte[])frame.Clone()); sound.Writer.TryWrite(ab); sent++;
                }
                Thread.Sleep(1000 / FPS / 2);
            }
            video.Writer.TryComplete(); sound.Writer.TryComplete();
        }) { IsBackground = true, Name = "group-clock" };
        clock.Start();
        e.EnableRaisingEvents = true;
        e.Exited += (_, _) =>
        {
            cts.Cancel();
            lock (gate)
            {
                if (enc != e) return;
                enc = null; layoutKey = "";
                var alive = inputs.Values.Any(y => !y.Ended);
                // it stopped on its own while people are still sending (out of memory, a crash): start it again, same live stream
                if (!stopping && alive && encFails < 5)
                {
                    encFails = frags > 60 ? 1 : encFails + 1;
                    Log($"the encoder stopped (exit {Try(() => e.ExitCode)}); restarting it, the stream stays live");
                    _ = Task.Delay(1000).ContinueWith(_ => { lock (gate) { if (enc != null || stopping) return; Relayout(); EncStart(false); } });
                    return;
                }
                if (!stopping && alive) Log($"the encoder keeps stopping (exit {Try(() => e.ExitCode)}); ending the stream. Is this computer out of memory or CPU?");
                var o2 = output; output = null; o2?.End(); Log("ended");
            }
        };
    }
    void Stop()
    {
        stopping = true; clockCts?.Cancel();
        foreach (var x in inputs.Values) Kill(x);
    }

    // ---- the picture
    static readonly byte[] Black = MakeBlack();
    static byte[] MakeBlack() { var f = new byte[GW * GH * 3 / 2]; Array.Fill(f, (byte)16, 0, GW * GH); Array.Fill(f, (byte)128, GW * GH, GW * GH / 2); return f; }
    void Compose(byte[] frame, int[] mix, long n)
    {
        Black.CopyTo(frame, 0); Array.Clear(mix);
        List<Input> everyone, shown; List<Tile> t; JsonObject st;
        lock (gate) { everyone = all; shown = list; t = tiles; st = Studio(); }
        foreach (var x in everyone)
        {   // everyone advances (off air too: their preview moves, and isn't heard)
            var a = Playout(x); var i = shown.IndexOf(x); if (i < 0 || i >= t.Count) continue;
            var T = t[i]; if (x.Last != null) DrawTile(frame, x.Last, x.LastW, x.LastH, T.X, T.Y, T.W, T.H);
            if (a != null && !x.Muted) for (int k = 0; k + 1 < a.Length && k / 2 < mix.Length; k += 2) mix[k >> 1] += (short)(a[k] | a[k + 1] << 8);
        }
        var tk = st["ticker"] as JsonObject; var lg = st["logo"] as JsonObject;
        var tickerOn = Js.IsTrue(tk?["on"]) && ticker != null;
        if (tickerOn) DrawTicker(frame, ticker!.Value, n, (int)(Js.Num(tk!["speed"]) ?? 4));
        if (Js.IsTrue(lg?["on"]) && logo != null) DrawLogo(frame, logo, (string?)lg!["pos"] ?? "tr", tickerOn);
    }
    /// <summary>A tile's frame into the picture (YUV 4:2:0 planes), resized nearest-neighbour when it isn't the tile's size yet.</summary>
    static void DrawTile(byte[] frame, byte[] img, int iw, int ih, int x0, int y0, int cw, int ch)
    {
        if (img.Length < iw * ih * 3 / 2) return;
        (int fo, int fs, int io, int isz, int sub)[] planes = [(0, GW, 0, iw, 1), (GW * GH, GW / 2, iw * ih, iw / 2, 2), (GW * GH * 5 / 4, GW / 2, iw * ih * 5 / 4, iw / 2, 2)];
        foreach (var (fo, fs, io, isz, sub) in planes)
        {
            int tw = cw / sub, th = ch / sub, sw = iw / sub, sh = ih / sub, ox = x0 / sub, oy = y0 / sub;
            if (sw == tw && sh == th) { for (int y = 0; y < th; y++) Buffer.BlockCopy(img, io + y * isz, frame, fo + (oy + y) * fs + ox, tw); continue; }
            for (int y = 0; y < th; y++)
            {
                int sy = Math.Min(sh - 1, y * sh / th), row = fo + (oy + y) * fs + ox, srow = io + sy * isz;
                for (int x = 0; x < tw; x++) frame[row + x] = img[srow + Math.Min(sw - 1, x * sw / tw)];
            }
        }
    }
    static void DrawTicker(byte[] frame, (byte[] Buf, int W) T, long n, int speed)
    {
        int y0 = GH - TICKER_H;
        Array.Fill(frame, (byte)26, y0 * GW, (GH - y0) * GW);
        Array.Fill(frame, (byte)128, GW * GH + y0 / 2 * GW / 2, (GH - y0) / 2 * GW / 2); Array.Fill(frame, (byte)128, GW * GH * 5 / 4 + y0 / 2 * GW / 2, (GH - y0) / 2 * GW / 2);
        long period = T.W + GW; int off = (int)(n * speed % period) & ~1, sx = GW - off, x0 = Math.Max(0, sx), x1 = Math.Min(GW, sx + T.W);
        if (x1 <= x0) return;
        (int fo, int fs, int to, int ts, int rows, int sub)[] planes = [(0, GW, 0, T.W, TICKER_H, 1), (GW * GH, GW / 2, T.W * TICKER_H, T.W / 2, TICKER_H / 2, 2), (GW * GH * 5 / 4, GW / 2, T.W * TICKER_H * 5 / 4, T.W / 2, TICKER_H / 2, 2)];
        foreach (var (fo, fs, to, ts, rows, sub) in planes)
            for (int y = 0; y < rows; y++) Buffer.BlockCopy(T.Buf, to + y * ts + (x0 - sx) / sub, frame, fo + (y0 / sub + y) * fs + x0 / sub, (x1 - x0) / sub);
    }
    static void DrawLogo(byte[] frame, byte[] L, string pos, bool tickerOn)
    {
        int A = LOGO_W * LOGO_H * 3 / 2, m = 16, lx = pos.Contains('r') ? GW - LOGO_W - m : m, ly = pos.Contains('b') ? GH - LOGO_H - m - (tickerOn ? TICKER_H : 0) : m;
        for (int y = 0; y < LOGO_H; y++) for (int x = 0; x < LOGO_W; x++) { int a = L[A + y * LOGO_W + x]; if (a == 0) continue; int d = (ly + y) * GW + lx + x; frame[d] = (byte)((L[y * LOGO_W + x] * a + frame[d] * (255 - a)) / 255); }
        for (int y = 0; y < LOGO_H / 2; y++) for (int x = 0; x < LOGO_W / 2; x++)
        {
            int a = L[A + 2 * y * LOGO_W + 2 * x]; if (a == 0) continue;
            foreach (var (fo, lo) in new[] { (GW * GH, LOGO_W * LOGO_H), (GW * GH * 5 / 4, LOGO_W * LOGO_H * 5 / 4) }) { int d = fo + (ly / 2 + y) * (GW / 2) + lx / 2 + x; frame[d] = (byte)((L[lo + y * (LOGO_W / 2) + x] * a + frame[d] * (255 - a)) / 255); }
        }
    }
    static async Task<byte[]> RunFfmpeg(IEnumerable<string> args, byte[]? input = null)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!; p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        var ms = new MemoryStream(); var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        if (input != null) { try { await p.StandardInput.BaseStream.WriteAsync(input); } catch { } }
        try { p.StandardInput.Close(); } catch { }
        await copy.WaitAsync(TimeSpan.FromSeconds(20)); await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return ms.ToArray();
    }
    /// <summary>A ticker: ffmpeg renders the text once (any font, any language); the compositor scrolls it along the bottom.</summary>
    async Task BuildTicker(string text)
    {
        if (string.IsNullOrEmpty(text)) { ticker = null; return; }
        int est = Math.Min(8000, (text.Length * 22 + 48 + 1) / 2 * 2); var f = Path.Combine(node.Options.DataDir, $"ticker-{Environment.ProcessId}.txt");
        await File.WriteAllTextAsync(f, text);
        var buf = await RunFfmpeg(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"color=c=0x1a1a1a:s={est}x{TICKER_H}:d=1", "-vf", $"drawtext=textfile={f.Replace(":", "\\:")}:fontcolor=white:fontsize=28:x=16:y=(h-text_h)/2", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuv420p", "pipe:1"]);
        if (buf.Length < est * TICKER_H * 3 / 2) { ticker = null; return; }
        int right = 0; for (int y = 0; y < TICKER_H; y++) for (int x = est - 1; x > right; x--) if (buf[y * est + x] > 60) { right = x; break; }
        int w = Math.Min(est, (right + 40 + 1) / 2 * 2); var o = new byte[w * TICKER_H * 3 / 2];
        foreach (var (so, ss, dO, ds, rows) in new[] { (0, est, 0, w, TICKER_H), (est * TICKER_H, est / 2, w * TICKER_H, w / 2, TICKER_H / 2), (est * TICKER_H * 5 / 4, est / 2, w * TICKER_H * 5 / 4, w / 2, TICKER_H / 2) })
            for (int y = 0; y < rows; y++) Buffer.BlockCopy(buf, so + y * ss, o, dO + y * ds, ds);
        ticker = (o, w);
    }
    /// <summary>A logo: ffmpeg turns the image into a 160×90 overlay with transparency once; the compositor blends it in.</summary>
    async Task BuildLogo(string? file)
    {
        if (file == null || !File.Exists(file)) { logo = null; return; }
        var b = await RunFfmpeg(["-hide_banner", "-loglevel", "error", "-i", file, "-vf", $"scale={LOGO_W}:{LOGO_H}:force_original_aspect_ratio=decrease,pad={LOGO_W}:{LOGO_H}:(ow-iw)/2:(oh-ih)/2:color=black@0,format=yuva420p", "-frames:v", "1", "-f", "rawvideo", "pipe:1"]);
        logo = b.Length >= LOGO_W * LOGO_H * 5 / 2 ? b : null;
    }
    /// <summary>The logo image: the one the host set, else the channel's avatar.</summary>
    string? LogoFile()
    {
        var st = Studio(); if ((string?)st["logo"]!["file"] is { } f && File.Exists(f)) return f;
        var chain = (string?)Profile["channel"]; if (chain == null) return null;
        var av = node.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var c) ? (string?)Derived.Of(c).Profile["avatar"] : null);
        var m = System.Text.RegularExpressions.Regex.Match(av ?? "", "^data:image/(png|jpeg|webp|gif);base64,(.+)$"); if (!m.Success) return null;
        var file = Path.Combine(node.Options.DataDir, $"avatar-{chain[..16]}.{m.Groups[1].Value}");
        try { File.WriteAllBytes(file, Convert.FromBase64String(m.Groups[2].Value)); return file; } catch { return null; }
    }
    /// <summary>A small JPEG of an input's latest frame (the host's preview).</summary>
    static async Task<string?> Thumb(Input x)
    {
        if (x.Last == null) return null;
        var j = await RunFfmpeg(["-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", $"{x.LastW}x{x.LastH}", "-i", "pipe:0", "-vf", "scale=192:-2", "-frames:v", "1", "-q:v", "6", "-f", "mjpeg", "pipe:1"], x.Last);
        return j.Length > 0 ? "data:image/jpeg;base64," + Convert.ToBase64String(j) : null;
    }

    // ---- browser streams into the picture
    /// <summary>The host was already live from a browser when someone joined: their stream joins the picture, and once the group stream is on the channel, it points its viewers there (liveend with next).</summary>
    void AbsorbHostLive()
    {
        var chain = (string?)Profile["channel"]; if (chain == null) return;
        var live = node.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var c) ? Derived.Of(c).LiveNow.Select(b => (JsonObject)b.DeepClone()).ToList() : []);
        foreach (var lb in live)
        {
            var p = lb["payload"]!; var s = (string)p["stream"]!;
            if (p["group"] != null || p["relay"] != null || (string?)lb["author"] == node.NodeKey || absorbed.Contains(s)) continue;   // only the owner's own browser streams
            if (Now() - (Js.Num(lb["ts"]) ?? 0) > 12 * 3600000) continue;
            if (Pull(s, hostUser, "host (browser)", chain, (string?)p["hub"]) is string) continue;
            absorbed.Add(s); Log($"the host was already live: “{p["title"]}” joins the picture");
            if (output == null) Profile["_absorbedTitle"] = p["title"]?.DeepClone();
        }
        if (absorbed.Count == 0 || pointing) return;
        pointing = true;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 60 && output?.Block == null; i++) await Task.Delay(2000);
            var blk = output?.Block;
            if (blk != null)
                foreach (var s in absorbed.ToList())
                    if (!node.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var c) && Derived.Of(c).LiveEnds.ContainsKey(s)))
                        try { node.Engine.Append(chain, new JsonObject { ["type"] = "liveend", ["stream"] = s, ["outcome"] = "ended", ["next"] = blk }); } catch (Exception e) { Log("pointing viewers: " + e.Message); }
            pointing = false;
        });
    }
    /// <summary>A browser stream sent into the group: watched from its hub like a relay, its segments (WebM) fed to the decoder in order. A string: why not.</summary>
    public object Pull(string stream, string user, string label, string chain, string? hubNode)
    {
        ClientWebSocket? ws = null; var cts = new CancellationTokenSource();
        var sink = AddInput("web:" + user + ":" + stream[..8], label, user, "web", "webm", () => { cts.Cancel(); try { ws?.Abort(); } catch { } });
        if (sink == null) return "The group picture is full (9).";
        var url = node.Relays!.HubUrlFor(chain, stream, hubNode);
        _ = Task.Run(async () =>
        {
            long last = -1; int missing = 0;
            var outbox = System.Threading.Channels.Channel.CreateUnbounded<string>();
            void Send(JsonObject m) => outbox.Writer.TryWrite(m.ToJsonString());
            void Watch() => Send(new JsonObject { ["t"] = "watch", ["stream"] = stream, ["segOnly"] = true, ["media"] = false, ["dcSlots"] = 0, ["slots"] = 0, ["relay"] = true });
            _ = Task.Run(async () => { while (!cts.IsCancellationRequested) { await Task.Delay(15000, cts.Token).ContinueWith(_ => { }); Send(new JsonObject { ["t"] = "alive", ["stream"] = stream }); } });
            while (!cts.IsCancellationRequested)
            {
                ws = new ClientWebSocket();
                try
                {
                    await ws.ConnectAsync(new Uri(url), cts.Token);
                    var w = ws; _ = Task.Run(async () => { await foreach (var m in outbox.Reader.ReadAllAsync(cts.Token)) { if (w.State != WebSocketState.Open) break; await w.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, cts.Token); } });
                    var buf = new byte[1 << 16]; using var ms = new MemoryStream();
                    while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                    {
                        var r = await ws.ReceiveAsync(buf, cts.Token); if (r.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buf, 0, r.Count); if (!r.EndOfMessage) continue;
                        JsonObject? m = null; try { m = JsonNode.Parse(ms.ToArray()) as JsonObject; } catch { } ms.SetLength(0);
                        switch ((string?)m?["t"])
                        {
                            case "hi": Send(new JsonObject { ["t"] = "auth", ["key"] = node.NodeKey, ["sig"] = node.Identity!.Sign("opentube-live-auth:" + (string?)m!["nonce"]) }); Watch(); break;
                            case "seg" when m!["seg"] is JsonObject g && (string?)g["stream"] == stream && Js.Num(g["n"]) is double n && n > last && (string?)g["d"] is { } d:
                                missing = 0; var b = Convert.FromBase64String(d);
                                if (n == 0) { last = 0; sink.Init(b, "video/webm"); } else if (last >= 0) { last = (long)n; sink.Fragment(b, false); }
                                break;
                            case "ended": Log($"{label} stopped"); sink.End(); cts.Cancel(); break;
                            case "nostream":   // the browser has only just started: ask again for a minute
                                if (++missing > 20) { Log($"{label}: the hub ({url}) doesn’t have their stream; giving up"); sink.End(); cts.Cancel(); }
                                else _ = Task.Delay(3000).ContinueWith(_ => Watch());
                                break;
                        }
                    }
                }
                catch { }
                if (!cts.IsCancellationRequested) await Task.Delay(2000).ContinueWith(_ => { });
            }
        });
        Log($"pulling {label}’s browser stream into the picture");
        return sink;
    }
    public int Kick(Func<Input, bool> pred)
    {
        List<Input> xs; lock (gate) xs = inputs.Values.Where(x => !x.Ended && pred(x)).ToList();
        foreach (var x in xs) { try { x.Close?.Invoke(); } catch { } EndInput(x); }
        return xs.Count;
    }

    // ---- control from the site (the host's studio, invitations, joining)
    public async Task<JsonObject> Control(string cmd, JsonObject a, string user, bool isHost, bool owner)
    {
        var users = (JsonObject)obs.Store["users"]!;
        string Label(string u) => u == hostUser ? "host" : (string?)users[u]?["name"] is { Length: > 0 } n ? n : u[..8];
        switch (cmd)
        {
            case "group": break;
            case "group.set":
                if (!isHost) throw new Exception("only the host can switch the group stream");
                if (a["on"] is JsonValue on && on.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) { Profile["tile"]!["on"] = Js.IsTrue(on); obs.Save(); }
                break;
            case "group.invite":
            {
                if (!isHost) throw new Exception("only the host can invite");
                var key = Js.Str(a["key"]); if (key == null || !Js.IsKey(a["key"]) || key == node.OwnerUser || key == user) throw new Exception("not a user ID");
                if (!On && Media.HasFfmpeg) { Profile["tile"]!["on"] = true; obs.Save(); }   // inviting someone on camera switches the group stream on
                if (users[key] is JsonObject su && su["guestOf"] == null)
                {
                    var can = su["can"] as JsonArray ?? []; if (!can.Any(x => (string?)x == "obs")) { can.Add("obs"); su["can"] = can; obs.Save(); node.AnnounceSoon(); }
                    Log($"{Js.Str(a["name"]) ?? key[..8]} shares this computer: they can join the group from the live page");
                }
                else
                {
                    var hours = Math.Min(Math.Max(Js.Num(a["hours"]) ?? 6, 0.25), 24 * 14);
                    users[key] = new JsonObject { ["can"] = new JsonArray("obs"), ["name"] = (Js.Str(a["name"]) ?? "")[..Math.Min(60, (Js.Str(a["name"]) ?? "").Length)], ["guestOf"] = user, ["until"] = Now() + hours * 3600000 };
                    obs.ProfileOf(key); obs.Save(); node.AnnounceSoon();
                    Log($"the host invited {Js.Str(a["name"]) ?? key[..8]} for {hours} h");
                }
                break;
            }
            case "group.uninvite":
            {   // hang up: disconnect and end their access
                var key = Js.Str(a["key"]) ?? ""; var u = users[key] as JsonObject;
                if ((string?)u?["guestOf"] is not { } gof || (gof != user && !owner)) throw new Exception("not your guest");
                Kick(x => x.User == key); users.Remove(key); ((JsonObject)obs.Store["profiles"]!).Remove(key); obs.Save(); node.AnnounceSoon();
                Log($"invitation of {(string?)u!["name"] ?? key[..8]} cancelled");
                break;
            }
            case "group.studio": case "group.air": case "group.mute": case "group.logo":
            {   // the host's studio controls
                if (!isHost) throw new Exception("only the host controls the picture");
                var st = Studio(); Input? x; lock (gate) x = Js.Str(a["id"]) is { } id ? inputs.GetValueOrDefault(id) : null;
                if (cmd == "group.air") { if (x == null) throw new Exception("not connected"); x.OnAir = Js.IsTrue(a["on"]); if (!x.OnAir && (string?)st["spot"] == x.Id) st["spot"] = null; Log($"{x.Label} {(x.OnAir ? "on air" : "off air (waiting)")}"); }
                else if (cmd == "group.mute") { if (x == null) throw new Exception("not connected"); x.Muted = Js.IsTrue(a["mute"]); }
                else if (cmd == "group.logo")
                {   // an image from the host's browser (a data URL, kept on this computer)
                    var m = System.Text.RegularExpressions.Regex.Match(Js.Str(a["data"]) ?? "", "^data:image/(png|jpeg|webp|gif);base64,([A-Za-z0-9+/=]+)$");
                    if (!m.Success) st["logo"]!["file"] = null;   // removed: back to the channel's avatar
                    else
                    {
                        var bytes = Convert.FromBase64String(m.Groups[2].Value); if (bytes.Length > 400000) throw new Exception("the logo is too big (400 KB at most)");
                        var f = Path.Combine(node.Options.DataDir, $"logo-{hostUser[..16]}.{m.Groups[1].Value}"); await File.WriteAllBytesAsync(f, bytes); st["logo"]!["file"] = f; st["logo"]!["on"] = true;
                    }
                    await BuildLogo(LogoFile());
                }
                else
                {
                    if (Js.Str(a["layout"]) is "grid" or "spotlight" or "solo") st["layout"] = Js.Str(a["layout"]);
                    if (a.ContainsKey("spot")) { st["spot"] = Js.Str(a["spot"]) is { Length: > 0 } sp ? sp : null; lock (gate) if ((string?)st["spot"] is { } sid && inputs.TryGetValue(sid, out var sx)) sx.OnAir = true; }
                    if (Js.IsBool(a["waiting"])) st["waiting"] = Js.IsTrue(a["waiting"]);
                    if (a["ticker"] is JsonObject t)
                    {
                        var tk = (JsonObject)st["ticker"]!;
                        var newText = Js.Str(t["text"]) is { } tx ? System.Text.RegularExpressions.Regex.Replace(tx, "[\r\n]+", "   ")[..Math.Min(500, System.Text.RegularExpressions.Regex.Replace(tx, "[\r\n]+", "   ").Length)] : (string?)tk["text"] ?? "";
                        if (Js.IsBool(t["on"])) tk["on"] = Js.IsTrue(t["on"]);
                        if (Js.Num(t["speed"]) is double sp2 && double.IsFinite(sp2)) tk["speed"] = Math.Min(12, Math.Max(1, Math.Round(sp2)));
                        if (newText != (string?)tk["text"] || (Js.IsTrue(tk["on"]) && ticker == null)) { tk["text"] = newText; await BuildTicker(newText); }
                    }
                    if (a["logo"] is JsonObject l)
                    {
                        var lg = (JsonObject)st["logo"]!;
                        if (Js.IsBool(l["on"])) lg["on"] = Js.IsTrue(l["on"]); if (Js.Str(l["pos"]) is "tl" or "tr" or "bl" or "br") lg["pos"] = Js.Str(l["pos"]);
                        if (Js.IsTrue(lg["on"]) && logo == null) await BuildLogo(LogoFile());
                    }
                }
                obs.Save(); lock (gate) if (enc != null) { layoutKey = ""; Relayout("the host changed the picture"); }
                break;
            }
            case "group.kick.all": if (!isHost) throw new Exception("only the host can end the group stream"); Kick(_ => true); break;
            case "group.kick":
                if (!isHost && Js.Str(a["id"]) is { } kid && !kid.Contains(user)) throw new Exception("only the host can disconnect others");
                if (Kick(x => x.Id == Js.Str(a["id"])) == 0) throw new Exception("not connected");
                break;
            case "group.join":
            {   // a browser stream (host's or a guest's) into the picture: we pull its segments
                if (!Media.HasFfmpeg) throw new Exception("group streams need ffmpeg on this computer");
                if (!On) { Profile["tile"]!["on"] = true; obs.Save(); Log("switched on: someone joined on camera"); }
                if (!Js.IsHex(a["stream"], 64) || !Js.IsHex(a["chain"], 64)) throw new Exception("bad stream");
                var chain = (string)a["chain"]!;
                var ok = node.Engine.Read(e => { var o = e.Store.Chains.TryGetValue(chain, out var c) ? c.Owner : e.Store.Directory.TryGetValue(chain, out var d) ? (string?)d.Genesis["author"] : null; return o != null && (o == user || e.Store.Ledger.Resolve(o) == e.Store.Ledger.Resolve(user)); });
                if (!ok && Js.Str(a["channelSig"]) is { } cs)
                {
                    var key = node.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var c) ? c.Owner : e.Store.Directory.TryGetValue(chain, out var d) ? (string?)d.Genesis["author"] : null);
                    ok = key != null && Rules.VerifySig(key, $"opentube-obs-channel:{node.NodeKey}:{user}:{chain}", cs);
                }
                if (!ok) throw new Exception("that stream’s channel isn’t yours");
                var hub = Js.IsKey(a["hub"]) ? (string?)a["hub"] : null;   // the hub the browser streams on (its live block may not have reached us yet)
                if (Pull((string)a["stream"]!, user, Label(user) + " (browser)", chain, hub) is string why) throw new Exception(why);
                break;
            }
            default: throw new Exception("this computer doesn’t know that command");
        }
        return await State(user, isHost, Js.IsTrue(a["thumbs"]));
    }
    async Task<JsonObject> State(string user, bool isHost, bool thumbs)
    {
        var users = (JsonObject)obs.Store["users"]!; var now = Now();
        List<Input> xs; List<Input> shown; lock (gate) { xs = inputs.Values.Where(x => !x.Ended).ToList(); shown = list; }
        var ins = new JsonArray();
        foreach (var x in xs)
        {
            var o = new JsonObject { ["id"] = x.Id, ["label"] = x.Label, ["user"] = x.User, ["kind"] = x.Kind, ["since"] = x.At, ["guest"] = users[x.User]?["guestOf"] != null, ["onAir"] = x.OnAir, ["muted"] = x.Muted, ["shown"] = shown.Contains(x) };
            if (isHost && thumbs) o["thumb"] = await Thumb(x);
            ins.Add(o);
        }
        var data = new JsonObject
        {
            ["on"] = On, ["host"] = isHost, ["live"] = Live, ["starting"] = Starting, ["ffmpeg"] = Media.HasFfmpeg, ["inputs"] = ins,
            ["me"] = new JsonArray(xs.Where(x => x.User == user).Select(x => (JsonNode?)new JsonObject { ["id"] = x.Id, ["onAir"] = x.OnAir, ["shown"] = shown.Contains(x) }).ToArray()),
            ["guests"] = new JsonArray(users.Where(kv => (string?)kv.Value?["guestOf"] == hostUser && !(Js.Num(kv.Value?["until"]) < now)).Select(kv => (JsonNode?)new JsonObject { ["key"] = kv.Key, ["name"] = kv.Value?["name"]?.DeepClone(), ["until"] = kv.Value?["until"]?.DeepClone() }).ToArray()),
        };
        if (isHost)
        {
            var st = Studio(); var lg = (JsonObject)st["logo"]!;
            data["studio"] = new JsonObject { ["layout"] = st["layout"]?.DeepClone(), ["spot"] = st["spot"]?.DeepClone(), ["waiting"] = st["waiting"]?.DeepClone(), ["ticker"] = st["ticker"]?.DeepClone(), ["logo"] = new JsonObject { ["on"] = lg["on"]?.DeepClone(), ["pos"] = lg["pos"]?.DeepClone(), ["has"] = LogoFile() != null, ["own"] = lg["file"] != null } };
        }
        else
        {
            var guestOf = (string?)users[user]?["guestOf"];
            data["invitedBy"] = hostUser == node.OwnerUser ? "the owner" : (string?)users[hostUser]?["name"] ?? hostUser[..8];
            data["until"] = guestOf != null ? users[user]?["until"]?.DeepClone() : null; data["joined"] = guestOf == null;
            data["key"] = obs.ProfileOf(user)["key"]?.DeepClone(); data["port"] = node.Rtmp?.Port;
        }
        return data;
    }
}
