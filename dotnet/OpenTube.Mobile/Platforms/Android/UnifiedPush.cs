using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using OpenTube.Client;
using OpenTube.Core;

namespace OpenTube.Mobile;

/// <summary>
/// UnifiedPush (unifiedpush.org), the open way for Android apps to get push messages without Google: a distributor app
/// (ntfy, NextPush, …) keeps one connection for all apps, and wakes this one when its push address gets a message.
/// This is the connector side of its protocol, as broadcasts: REGISTER to the distributor; NEW_ENDPOINT, MESSAGE and
/// UNREGISTERED back. The messages are Web Push from your computers, decrypted here with this device's keys.
/// </summary>
public static class UnifiedPush
{
    public const string Register = "org.unifiedpush.android.distributor.REGISTER";
    public const string NewEndpoint = "org.unifiedpush.android.connector.NEW_ENDPOINT", Message = "org.unifiedpush.android.connector.MESSAGE",
        Unregistered = "org.unifiedpush.android.connector.UNREGISTERED", Failed = "org.unifiedpush.android.connector.REGISTRATION_FAILED";
    static string Dir(Context c) => Path.Combine(c.FilesDir!.AbsolutePath, "chain", "kv");
    public static string? Distributor(Context c) => File.Exists(Path.Combine(Dir(c), "upDistributor")) ? File.ReadAllText(Path.Combine(Dir(c), "upDistributor")) : null;
    static string Token(Context c)
    {
        var f = Path.Combine(Dir(c), "upToken"); Directory.CreateDirectory(Dir(c));
        if (!File.Exists(f)) File.WriteAllText(f, Guid.NewGuid().ToString());
        return File.ReadAllText(f);
    }
    public static event Action<string?>? EndpointChanged;

    /// <summary>Distributor apps on this phone.</summary>
    public static List<string> Distributors(Context c) =>
        c.PackageManager!.QueryBroadcastReceivers(new Intent(Register), 0).Select(r => r.ActivityInfo?.PackageName).OfType<string>().Distinct().ToList();

    public static void Start(Context c, string distributor)
    {
        Directory.CreateDirectory(Dir(c)); File.WriteAllText(Path.Combine(Dir(c), "upDistributor"), distributor);
        var i = new Intent(Register).SetPackage(distributor)
            .PutExtra("token", Token(c)).PutExtra("application", c.PackageName).PutExtra("message", "OpenTube notifications")
            .PutExtra("pi", PendingIntent.GetBroadcast(c, 0, new Intent("org.unifiedpush.android.connector.pi"), PendingIntentFlags.Immutable));
        c.SendBroadcast(i);
    }

    /// <summary>This device's push keys, read from the app's files (a message can arrive while the app isn't running).</summary>
    internal static (byte[] Pub, byte[] Priv, byte[] Auth)? Keys(Context c)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(Dir(c), "pushDevice"))) is not JsonObject o) return null;
            return (B64u.Decode((string)o["pub"]!), B64u.Decode((string)o["priv"]!), B64u.Decode((string)o["auth"]!));
        }
        catch { return null; }
    }

    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter([NewEndpoint, Message, Unregistered, Failed])]
    public sealed class Receiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context == null || intent == null || intent.GetStringExtra("token") != Token(context)) return;
            switch (intent.Action)
            {
                case NewEndpoint: EndpointChanged?.Invoke(intent.GetStringExtra("endpoint")); SaveEndpoint(context, intent.GetStringExtra("endpoint")); break;
                case Unregistered: case Failed: EndpointChanged?.Invoke(null); SaveEndpoint(context, null); break;
                case Message:
                {
                    var body = intent.GetByteArrayExtra("bytesMessage"); if (body == null || Keys(context) is not { } k) return;
                    var n = PushLink.Read(body, k.Pub, k.Priv, k.Auth); if (n == null || MainActivity.Visible) return;   // (on screen: the app shows it itself)
                    AndroidNotifier.Show(context, n);
                    break;
                }
            }
        }
        static void SaveEndpoint(Context c, string? endpoint)
        {   // the app's PushLink picks it up next start too
            try
            {
                var f = Path.Combine(Dir(c), "pushDevice"); var o = File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonObject ?? [] : [];
                o["endpoint"] = endpoint; File.WriteAllText(f, o.ToJsonString());
            }
            catch { }
        }
    }
}

/// <summary>The You page's "notifications while the app is closed": choosing the distributor, and where it stands.</summary>
public sealed class AndroidPushSetup : OpenTube.UI.IPushSetup
{
    readonly PushLink link;
    public event Action? Changed;
    public AndroidPushSetup(PushLink link)
    {
        this.link = link; link.Changed += () => Changed?.Invoke();
        UnifiedPush.EndpointChanged += e => _ = link.SetEndpoint(e);
    }
    public IReadOnlyList<string> Distributors() => UnifiedPush.Distributors(Android.App.Application.Context);
    public string Status => UnifiedPush.Distributor(Android.App.Application.Context) == null ? "Off" : link.Status;
    public Task Enable(string distributor) { UnifiedPush.Start(Android.App.Application.Context, distributor); return Task.CompletedTask; }
}
