using NAudio.CoreAudioApi;

namespace JriksSoundboard.Core.Audio;

public sealed record AudioDeviceInfo(string Id, string Name)
{
    public bool IsVbCable => Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
                             || Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

/// <summary>Lists active Windows audio endpoints and finds the VB-Cable pair.</summary>
public static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => List(DataFlow.Render);

    public static IReadOnlyList<AudioDeviceInfo> GetInputDevices() => List(DataFlow.Capture);

    /// <summary>VB-Cable's playback side ("CABLE Input") — the app plays sounds into this.</summary>
    public static AudioDeviceInfo? FindVbCableInput() =>
        GetOutputDevices().FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));

    /// <summary>VB-Cable's recording side ("CABLE Output") — the game/Steam uses this as its mic.</summary>
    public static AudioDeviceInfo? FindVbCableOutput() =>
        GetInputDevices().FirstOrDefault(d => d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));

    public static AudioDeviceInfo? GetDefaultOutput() => GetDefault(DataFlow.Render);

    public static AudioDeviceInfo? GetDefaultInput() => GetDefault(DataFlow.Capture);

    private static IReadOnlyList<AudioDeviceInfo> List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDeviceInfo>();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
            }
        }
        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static AudioDeviceInfo? GetDefault(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out MMDevice? device) || device is null)
        {
            return null;
        }
        using (device)
        {
            return new AudioDeviceInfo(device.ID, device.FriendlyName);
        }
    }
}
