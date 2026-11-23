using OpenTube.Node;
using Terminal.Gui;

namespace OpenTube.App;

/// <summary>The node in a terminal (SSH, servers): menus, live status, peers and the log, drawn in text.</summary>
public static class TerminalUi
{
    public static int Run(string[] args)
    {
        var server = new NodeServer(NodeOptions.From(args));
        server.Node.Echo = false;   // the screen is ours
        var start = server.StartAsync();

        Application.Init();
        var top = Application.Top;
        var status = new Label("Starting…") { X = 1, Y = 0, Width = Dim.Fill(1), Height = 7 };
        var peersList = new ListView(new List<string>()) { X = 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill() };
        var logList = new ListView(new List<string>()) { X = 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill() };
        var statusFrame = new FrameView("Node") { X = 0, Y = 1, Width = Dim.Fill(), Height = 9 };
        var peersFrame = new FrameView("Connected") { X = 0, Y = Pos.Bottom(statusFrame), Width = Dim.Percent(35), Height = Dim.Fill(1) };
        var logFrame = new FrameView("Log") { X = Pos.Right(peersFrame), Y = Pos.Bottom(statusFrame), Width = Dim.Fill(), Height = Dim.Fill(1) };
        statusFrame.Add(status); peersFrame.Add(peersList); logFrame.Add(logList);

        void Message(string title, string text) => MessageBox.Query(Math.Min(100, Application.Driver.Cols - 4), Math.Min(30, Application.Driver.Rows - 4), title, text, "OK");
        void AddPeer()
        {
            var input = new TextField("") { X = 1, Y = 2, Width = Dim.Fill(1) };
            var ok = new Button("Connect", is_default: true); var cancel = new Button("Cancel");
            var d = new Dialog("Connect to another node", 64, 9, ok, cancel);
            d.Add(new Label("An IP address (1.2.3.4 or 1.2.3.4:8080), an https address, or ws://…/ot") { X = 1, Y = 0, Width = Dim.Fill(1) }, input);
            ok.Clicked += () => { var a = input.Text.ToString()?.Trim(); if (!string.IsNullOrEmpty(a)) server.Node.AddPeer(a); Application.RequestStop(); };
            cancel.Clicked += () => Application.RequestStop();
            input.SetFocus(); Application.Run(d);
        }
        string Capture(Func<int> f)
        {
            var o = Console.Out; var w = new StringWriter(); Console.SetOut(w);
            try { f(); } finally { Console.SetOut(o); }
            return w.ToString();
        }
        var menu = new MenuBar([
            new MenuBarItem("_Node", [
                new MenuItem("_Connect to a node…", "", AddPeer),
                new MenuItem("_Website address", "", () => Message("Open in a browser", $"This computer:  http://localhost:{server.Node.Options.Port}\n" +
                    (server.Node.PublicUrl is { } u ? $"Anywhere:       {u}" : "No public address yet (install cloudflared for one)."))),
                null!,
                new MenuItem("_Quit", "Ctrl+Q", () => Application.RequestStop()),
            ]),
            new MenuBarItem("_Service", [
                new MenuItem("_Run at startup (install)", "", () => Message("Run at startup", Capture(() => ServiceInstaller.Install(args)))),
                new MenuItem("_Stop running at startup", "", () => Message("Run at startup", Capture(ServiceInstaller.Uninstall))),
                new MenuItem("_How to set it up by hand", "", () => Message("Run at startup", ServiceInstaller.Instructions(args))),
            ]),
        ]);
        top.Add(menu, statusFrame, peersFrame, logFrame,
            new StatusBar([new StatusItem(Key.CtrlMask | Key.Q, "~^Q~ Quit", () => Application.RequestStop()), new StatusItem(Key.F2, "~F2~ Connect to a node", AddPeer), new StatusItem(Key.F9, "~F9~ Menu", null)]));

        Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(1), _ =>
        {
            if (start.IsFaulted) status.Text = "Couldn't start: " + start.Exception?.InnerException?.Message;
            else
            {
                var s = server.Node.Status;
                status.Text = s.Count == 0 ? "Starting…" :
                    $"Network   {((string?)s["network"])?[..16]}…   OpenTube {s["version"]} on {s["impl"]}\n" +
                    $"Main      #{s["height"]}   {s["registered"]} registered   {s["chains"]} chains loaded   {s["pending"]} pending\n" +
                    $"Peers     {s["peers"]} connected   tracker {s["trackerClients"]}   live {s["liveClients"]}   nodes known {s["nodes"]}\n" +
                    $"Website   http://localhost:{server.Node.Options.Port}   {server.Node.PublicUrl ?? "(no public address)"}\n" +
                    $"Node key  {s["node"]}\n" +
                    $"Up        {TimeSpan.FromSeconds((long?)s["uptime"] ?? 0):d\\.hh\\:mm\\:ss}";
                peersList.SetSource(server.Node.PeerList.Where(p => p.Role is "node-in" or "node-out").Select(p => $"{(p.Role == "node-out" ? "→" : "←")} {p.Label}").ToList());
                var lines = server.Node.Logs.ToList(); logList.SetSource(lines);
                if (lines.Count > 0) logList.SelectedItem = lines.Count - 1;
            }
            return true;
        });
        // Ctrl+C stops it, as in any terminal program (the menus put the terminal in raw mode, so it arrives as a key)
        Application.RootKeyEvent += k => { if (k.Key == (Key.CtrlMask | Key.C) || k.Key == (Key.CtrlMask | Key.c)) { Application.RequestStop(); return true; } return false; };
        Application.Run();
        Application.Shutdown();
        server.DisposeAsync().AsTask().Wait(3000);
        return 0;
    }
}
