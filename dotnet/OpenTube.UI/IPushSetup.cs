namespace OpenTube.UI;

/// <summary>
/// Notifications while the app is closed (the phone app): your computers send them through a push service. On Android
/// that's a UnifiedPush distributor app (ntfy, or another), chosen here. Absent where there's nothing to set up.
/// </summary>
public interface IPushSetup
{
    /// <summary>The distributor apps installed (package names), for choosing one.</summary>
    IReadOnlyList<string> Distributors();
    string Status { get; }
    Task Enable(string distributor);
    event Action? Changed;
}
