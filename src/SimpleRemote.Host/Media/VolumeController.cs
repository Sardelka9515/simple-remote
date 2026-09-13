using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using SimpleRemote.Server.Protocol;

namespace SimpleRemote.Media;

/// <summary>
/// System volume, mute, and output-device selection through Core Audio.
///
/// Volume changes made on the PC are pushed to the phone as well as the other way round, so the
/// slider never shows a stale value - a one-way control that silently disagrees with reality is
/// worse than no control.
///
/// Volume itself is event-driven via the endpoint callback. Default-device switches are polled
/// instead: NAudio keeps IMMNotificationClient internal, so it cannot be implemented from outside
/// the assembly, and a device switch is a once-in-a-while user action rather than a hot signal.
/// </summary>
public sealed class VolumeController : IDisposable
{
    private static readonly TimeSpan DevicePollInterval = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private string? _deviceId;
    private CancellationTokenSource? _cts;

    public event Action<VolumeStateMessage>? StateChanged;

    /// <summary>False when Core Audio is unavailable, so callers can hide the controls rather than fail.</summary>
    public bool Available { get; private set; }

    public void Initialize()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            AttachDefaultDevice();
            Available = _device is not null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Available = false;
            return;
        }

        _cts = new CancellationTokenSource();
        _ = WatchDefaultDeviceAsync(_cts.Token);
    }

    private async Task WatchDefaultDeviceAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(DevicePollInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                string? currentDefault;
                try
                {
                    currentDefault = _enumerator?
                        .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
                }
                catch (COMException)
                {
                    continue;
                }

                if (currentDefault == _deviceId) continue;

                AttachDefaultDevice();
                StateChanged?.Invoke(Current());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void AttachDefaultDevice()
    {
        lock (_gate)
        {
            DetachLocked();

            try
            {
                _device = _enumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (_device is not null)
                {
                    _deviceId = _device.ID;
                    _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                }
            }
            catch (COMException)
            {
                // No render endpoint at all (headless, or every device disabled).
                _device = null;
                _deviceId = null;
            }
        }
    }

    private void DetachLocked()
    {
        if (_device is null) return;

        try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; }
        catch (COMException) { }

        _device.Dispose();
        _device = null;
        _deviceId = null;
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data) => StateChanged?.Invoke(Current());

    public VolumeStateMessage Current()
    {
        lock (_gate)
        {
            if (_device is null) return new VolumeStateMessage { Level = 0, Muted = true, Device = null };

            try
            {
                return new VolumeStateMessage
                {
                    Level = _device.AudioEndpointVolume.MasterVolumeLevelScalar,
                    Muted = _device.AudioEndpointVolume.Mute,
                    Device = _device.FriendlyName,
                };
            }
            catch (COMException)
            {
                return new VolumeStateMessage { Level = 0, Muted = true, Device = null };
            }
        }
    }

    public void SetLevel(double level)
    {
        lock (_gate)
        {
            if (_device is null) return;
            try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(level, 0d, 1d); }
            catch (COMException) { }
        }
    }

    public void SetMute(bool muted)
    {
        lock (_gate)
        {
            if (_device is null) return;
            try { _device.AudioEndpointVolume.Mute = muted; }
            catch (COMException) { }
        }
    }

    /// <summary>Active render endpoints, for the output picker.</summary>
    public IReadOnlyList<(string Id, string Name, bool IsDefault)> ListOutputs()
    {
        try
        {
            var currentId = _deviceId;
            return _enumerator?
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => (d.ID, d.FriendlyName, d.ID == currentId))
                .ToList() ?? [];
        }
        catch (COMException)
        {
            return [];
        }
    }

    /// <summary>
    /// Switches the default output device. Returns false when the undocumented policy interface is
    /// unavailable, which lets the UI show the device list read-only rather than offering a button
    /// that silently does nothing.
    /// </summary>
    public bool SetDefaultOutput(string deviceId)
    {
        if (!PolicyConfig.TrySetDefaultEndpoint(deviceId)) return false;

        AttachDefaultDevice();
        StateChanged?.Invoke(Current());
        return true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();

        lock (_gate)
        {
            DetachLocked();
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }
}

/// <summary>
/// Minimal binding to IPolicyConfig, the undocumented interface behind the Windows sound flyout.
///
/// There is no supported API for changing the default playback device, so this is the only way to
/// do it. Two vtable layouts have shipped and they differ by one slot, so both are declared and
/// the runtime QueryInterface picks. Every method is PreserveSig, so a wrong guess comes back as
/// an HRESULT rather than an exception.
/// </summary>
internal static class PolicyConfig
{
    private static readonly Guid ClsidPolicyConfigClient = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    public static bool TrySetDefaultEndpoint(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;

        object? client = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidPolicyConfigClient);
            if (type is null) return false;

            client = Activator.CreateInstance(type);

            // Console covers normal playback; setting Multimedia and Communications too keeps all
            // three roles consistent, which is what the Windows UI does when you pick a device.
            return client switch
            {
                IPolicyConfig modern => SetAllRoles(modern.SetDefaultEndpoint, deviceId),
                IPolicyConfigVista vista => SetAllRoles(vista.SetDefaultEndpoint, deviceId),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException
                                       or TypeLoadException or MissingMethodException)
        {
            return false;
        }
        finally
        {
            if (client is not null && Marshal.IsComObject(client)) Marshal.ReleaseComObject(client);
        }
    }

    private static bool SetAllRoles(Func<string, ERole, int> set, string deviceId) =>
        set(deviceId, ERole.Console) >= 0
        && set(deviceId, ERole.Multimedia) >= 0
        && set(deviceId, ERole.Communications) >= 0;

    internal enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceName, out nint format);
        [PreserveSig] int GetDeviceFormat(string deviceName, bool isDefault, out nint format);
        [PreserveSig] int ResetDeviceFormat(string deviceName);
        [PreserveSig] int SetDeviceFormat(string deviceName, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceName, bool isDefault, out nint defaultPeriod, out nint minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceName, nint period);
        [PreserveSig] int GetShareMode(string deviceName, out nint mode);
        [PreserveSig] int SetShareMode(string deviceName, nint mode);
        [PreserveSig] int GetPropertyValue(string deviceName, bool fxStore, nint key, out nint value);
        [PreserveSig] int SetPropertyValue(string deviceName, bool fxStore, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(string deviceName, bool visible);
    }

    [ComImport, Guid("568b9108-44bf-40b4-9006-86afe5b5a620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat(string deviceName, out nint format);
        [PreserveSig] int GetDeviceFormat(string deviceName, bool isDefault, out nint format);
        [PreserveSig] int SetDeviceFormat(string deviceName, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceName, bool isDefault, out nint defaultPeriod, out nint minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceName, nint period);
        [PreserveSig] int GetShareMode(string deviceName, out nint mode);
        [PreserveSig] int SetShareMode(string deviceName, nint mode);
        [PreserveSig] int GetPropertyValue(string deviceName, bool fxStore, nint key, out nint value);
        [PreserveSig] int SetPropertyValue(string deviceName, bool fxStore, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(string deviceName, bool visible);
    }
}
