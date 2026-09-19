using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AppPortal.Server.Options;

using Microsoft.Extensions.Options;

namespace AppPortal.Server.Devices;

public sealed class DeviceRecord
{
    public string Name { get; set; } = "";
    public string EndpointId { get; set; } = "";
    public string TokenSha256 { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DevicesFile
{
    public List<DeviceRecord> Devices { get; set; } = [];
}

/// <summary>
/// Devices that may call the API, each with the SHA-256 of its bearer token. The plaintext token is shown once,
/// when the device is added, and is never stored.
/// </summary>
public sealed class DeviceStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<DeviceStore>? _logger;
    private readonly object _gate = new();
    private DevicesFile _file = new();
    private DateTime _loadedStamp = DateTime.MinValue;

    public DeviceStore(IOptions<PortalOptions> options, IHostEnvironment env, ILogger<DeviceStore> logger)
        : this(Path.IsPathRooted(options.Value.DevicesPath) ? options.Value.DevicesPath : Path.Combine(env.ContentRootPath, options.Value.DevicesPath), logger)
    {
    }

    public DeviceStore(string path, ILogger<DeviceStore>? logger = null)
    {
        _path = path;
        _logger = logger;
    }

    public string Path_ => _path;

    public IReadOnlyList<DeviceRecord> All()
    {
        lock (_gate)
        {
            Reload();
            return _file.Devices.ToList();
        }
    }

    public DeviceRecord? Authenticate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = Hash(token);
        lock (_gate)
        {
            Reload();
            foreach (var device in _file.Devices)
            {
                if (device.Enabled && FixedTimeEquals(device.TokenSha256, hash))
                {
                    return device;
                }
            }
        }

        return null;
    }

    /// <summary>Adds a device and returns its plaintext token. Replaces an existing device of the same name.</summary>
    public string Add(string name, string endpointId)
    {
        var token = GenerateToken();
        lock (_gate)
        {
            Reload();
            _file.Devices.RemoveAll(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            _file.Devices.Add(new DeviceRecord
            {
                Name = name,
                EndpointId = endpointId,
                TokenSha256 = Hash(token),
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            Save();
        }

        return token;
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            Reload();
            var removed = _file.Devices.RemoveAll(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save();
            }

            return removed;
        }
    }

    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "apd_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var left = Encoding.ASCII.GetBytes(a);
        var right = Encoding.ASCII.GetBytes(b);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private void Reload()
    {
        if (!File.Exists(_path))
        {
            _file = new DevicesFile();
            _loadedStamp = DateTime.MinValue;
            return;
        }

        var stamp = File.GetLastWriteTimeUtc(_path);
        if (stamp == _loadedStamp)
        {
            return;
        }

        try
        {
            _file = JsonSerializer.Deserialize<DevicesFile>(File.ReadAllText(_path), Json) ?? new DevicesFile();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A half-written or unreadable file must not throw out of Authenticate, which would make
            // every API request fail with an unhandled exception. Keep serving the last good list.
            _logger?.LogError(ex, "Device file {Path} could not be read; keeping the previously loaded devices", _path);
        }

        _loadedStamp = stamp;
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write then rename, so a crash mid-write cannot leave a truncated device file behind.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_file, Json));
        File.Move(temp, _path, overwrite: true);
        _loadedStamp = File.GetLastWriteTimeUtc(_path);
    }
}
