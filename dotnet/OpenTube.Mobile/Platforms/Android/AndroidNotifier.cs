using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using OpenTube.Client;

namespace OpenTube.Mobile;

/// <summary>
/// OpenTube's notifications as Android notifications: each kind (new videos, live streams, comments, messages, the
/// market…) is its own notification channel, so people choose sound, silent or off per kind in Android's settings.
/// Tapping one opens its page in the app.
/// </summary>
public static class AndroidNotifier
{
    public const string HrefExtra = "opentube-href";
    static int nextId = 1000;
    static string ChannelId(string type) => "opentube-" + type;

    public static async Task Attach(ClientSession s)
    {
        Channels(Android.App.Application.Context);
        try { await MainThread.InvokeOnMainThreadAsync(async () => await Permissions.RequestAsync<Permissions.PostNotifications>()); } catch { }   // Android 13+ asks once
        s.Notes.Arrived += n => { if (!MainActivity.Visible) Show(Android.App.Application.Context, n); };
    }
    /// <summary>One notification channel per kind (made once; Android keeps the person's choices for each).</summary>
    static void Channels(Context ctx)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O || ctx.GetSystemService(Context.NotificationService) is not NotificationManager nm) return;
        foreach (var (key, label, _) in Notifications.Types)
        {
            var importance = key is "live" or "message" or "swap" ? NotificationImportance.High : key is "like" or "coins" ? NotificationImportance.Low : NotificationImportance.Default;
            nm.CreateNotificationChannel(new NotificationChannel(ChannelId(key), label, importance) { Description = "OpenTube: " + label });
        }
    }

    public static void Show(Context ctx, Notifications.Note n)
    {
        Channels(ctx);
        var open = new Intent(ctx, typeof(MainActivity)).PutExtra(HrefExtra, n.Href).AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var id = Interlocked.Increment(ref nextId);
        var pending = PendingIntent.GetActivity(ctx, id, open, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var b = new NotificationCompat.Builder(ctx, ChannelId(n.Type))
            .SetSmallIcon(Resource.Mipmap.appicon_foreground).SetContentTitle(Notifications.LabelOf(n.Type)).SetContentText(n.Text)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(n.Text)).SetContentIntent(pending).SetAutoCancel(true)
            .SetWhen((long)n.Ts).SetShowWhen(true).SetGroup("opentube-" + n.Type);
        try { NotificationManagerCompat.From(ctx).Notify(id, b.Build()); } catch { }   // (no permission: the app's bell still has it)
    }
}
