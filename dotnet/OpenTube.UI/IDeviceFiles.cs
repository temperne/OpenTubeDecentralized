namespace OpenTube.UI;

/// <summary>What differs between the browser and the phone for keys: saving a file (a download; the phone's share
/// sheet: save it, send it to yourself…) and starting over with another identity (a reload; relaunching the app).</summary>
public interface IDeviceFiles
{
    Task Save(string name, string text);
    Task Restart();
}
