using Microsoft.AspNetCore.Components;
using OpenTube.Client;

namespace OpenTube.UI.Shared;

/// <summary>A page that redraws when the network changes (at most 4 times a second).</summary>
public abstract class Live : ComponentBase, IDisposable
{
    [Inject] public ClientSession S { get; set; } = null!;
    [Inject] public NavigationManager Nav { get; set; } = null!;
    bool pending;
    protected override void OnInitialized() => S.Changed += Poke;
    void Poke()
    {
        if (pending) return; pending = true;
        _ = InvokeAsync(async () => { await Task.Delay(250); pending = false; StateHasChanged(); });
    }
    public virtual void Dispose() => S.Changed -= Poke;
    protected static string Ago(double ms)
    {
        var s = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ms) / 1000;
        return s < 60 ? "just now" : s < 3600 ? $"{(int)(s / 60)} min ago" : s < 86400 ? $"{(int)(s / 3600)} h ago" : $"{(int)(s / 86400)} days ago";
    }
    protected static string Bytes(double n) => n < 1e6 ? $"{n / 1e3:0} KB" : n < 1e9 ? $"{n / 1e6:0.0} MB" : $"{n / 1e9:0.00} GB";
}
