using System;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace StealYourVoice.Audio;

/// <summary>
/// Windows tells us when audio devices appear, disappear or the default one changes,
/// so we re-scan only then instead of polling every couple of seconds.
/// </summary>
public sealed class AudioWatch : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _en = new();

    /// <summary>Something changed since the flag was last cleared (starts dirty: nothing scanned yet).</summary>
    public static volatile bool DevicesDirty = true;
    public static volatile bool DefaultDirty = true;

    public AudioWatch()
    {
        try { _en.RegisterEndpointNotificationCallback(this); } catch { /* old Windows — callers still scan once */ }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => DevicesDirty = DefaultDirty = true;
    public void OnDeviceAdded(string pwstrDeviceId) => DevicesDirty = true;
    public void OnDeviceRemoved(string deviceId) => DevicesDirty = DefaultDirty = true;
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => DefaultDirty = true;
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { /* volume etc. — not interesting */ }

    public void Dispose()
    {
        try { _en.UnregisterEndpointNotificationCallback(this); } catch { /* ignore */ }
        _en.Dispose();
    }
}
