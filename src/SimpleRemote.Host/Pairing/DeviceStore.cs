using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimpleRemote.Config;

namespace SimpleRemote.Pairing;

/// <summary>A paired phone. Only the token <em>hash</em> is persisted.</summary>
public sealed class DeviceRecord
{
    public required string Id { get; set; }
    public required string TokenHash { get; set; }
    public string Name { get; set; } = "Device";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}

/// <summary>Result of a successful pairing: the only time the raw token exists.</summary>
public sealed record IssuedDevice(string DeviceId, string Token);

/// <summary>
/// Persistent registry of paired devices, stored at %APPDATA%\SimpleRemote\devices.json.
///
/// Tokens are stored as SHA-256 hashes so the file is not itself a credential. There is no
/// salt/KDF here on purpose: these are 256-bit random tokens, not user-chosen passwords, so they
/// are not dictionary-attackable and a slow KDF would only add latency to every reconnect.
/// </summary>
public sealed class DeviceStore
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<DeviceRecord> _devices = [];

    public DeviceStore(string? path = null, TimeProvider? time = null)
    {
        _path = path ?? Path.Combine(ConfigStore.DataDirectory, "devices.json");
        _time = time ?? TimeProvider.System;
        Load();
    }

    public event Action? Changed;

    public IReadOnlyList<DeviceRecord> Devices
    {
        get { lock (_gate) return _devices.ToList(); }
    }

    public IssuedDevice Register(string name)
    {
        var id = PairingTokenSource.Base64Url(RandomNumberGenerator.GetBytes(8));
        var token = PairingTokenSource.Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            _devices.Add(new DeviceRecord
            {
                Id = id,
                TokenHash = Hash(token),
                Name = Sanitize(name),
                CreatedUtc = now,
                LastSeenUtc = now,
            });
            SaveLocked();
        }

        Changed?.Invoke();
        return new IssuedDevice(id, token);
    }

    /// <summary>Verifies a device's token and refreshes its last-seen stamp.</summary>
    public DeviceRecord? Verify(string? deviceId, string? token)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(token)) return null;

        lock (_gate)
        {
            var device = _devices.FirstOrDefault(d => d.Id == deviceId);
            if (device is null) return null;

            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(device.TokenHash),
                    SHA256.HashData(Encoding.UTF8.GetBytes(token))))
                return null;

            device.LastSeenUtc = _time.GetUtcNow();
            return device;
        }
    }

    public bool Revoke(string deviceId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _devices.RemoveAll(d => d.Id == deviceId) > 0;
            if (removed) SaveLocked();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void Rename(string deviceId, string name)
    {
        lock (_gate)
        {
            var device = _devices.FirstOrDefault(d => d.Id == deviceId);
            if (device is null || device.Name == Sanitize(name)) return;
            device.Name = Sanitize(name);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Flushes last-seen stamps, which are mutated in-memory on every reconnect.</summary>
    public void Flush()
    {
        lock (_gate) SaveLocked();
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Sanitize(string name)
    {
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) return "Device";
        return name.Length > 40 ? name[..40] : name;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var records = JsonSerializer.Deserialize(File.ReadAllText(_path), AppJson.Default.DeviceRecordArray);
            if (records is not null) _devices.AddRange(records.Where(r => !string.IsNullOrEmpty(r.Id)));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt device list costs one re-pair; it must not stop the app from starting.
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(_devices.ToArray(), AppJson.Default.DeviceRecordArray);

            // Write-then-move so a crash mid-write cannot truncate the existing list.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
