namespace OpenTube.UI;

/// <summary>Where the pages run (shown in the header): the website in WebAssembly, or the phone app.</summary>
public sealed record UiPlatform(string Label);
