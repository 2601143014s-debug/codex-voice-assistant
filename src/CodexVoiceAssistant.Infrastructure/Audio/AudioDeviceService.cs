using CodexVoiceAssistant.Domain;
using NAudio.CoreAudioApi;

namespace CodexVoiceAssistant.Infrastructure.Audio;

public static class AudioDeviceService
{
    public static IReadOnlyList<AudioDeviceInfo> GetCaptureDevices() =>
        Enumerate(DataFlow.Capture);

    public static IReadOnlyList<AudioDeviceInfo> GetRenderDevices() =>
        Enumerate(DataFlow.Render);

    public static MMDevice GetCaptureDevice(string? id) =>
        GetDevice(DataFlow.Capture, id);

    public static MMDevice GetRenderDevice(string? id) =>
        GetDevice(DataFlow.Render, id);

    public static void EnsureCaptureReady(MMDevice device)
    {
        try
        {
            var volume = device.AudioEndpointVolume;
            if (volume.Mute)
            {
                volume.Mute = false;
            }
            if (volume.MasterVolumeLevelScalar < 0.98f)
            {
                volume.MasterVolumeLevelScalar = 1.0f;
            }
        }
        catch
        {
        }
    }

    private static IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(device => new AudioDeviceInfo(
                device.ID,
                device.FriendlyName))
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static MMDevice GetDevice(DataFlow flow, string? id)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                try
                {
                    return enumerator.GetDevice(id);
                }
                catch
                {
                }
            }

            var role = flow == DataFlow.Render
                ? Role.Multimedia
                : Role.Communications;
            return enumerator.GetDefaultAudioEndpoint(flow, role);
        }
        finally
        {
            enumerator.Dispose();
        }
    }
}
