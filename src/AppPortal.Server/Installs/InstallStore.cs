using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Options;
using AppPortal.Shared;

using Microsoft.Extensions.Options;

namespace AppPortal.Server.Installs;

public sealed class InstallRecord
{
    public string Id { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string EndpointId { get; set; } = "";
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public string? AutomationId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public InstallState State { get; set; }
    public int PercentComplete { get; set; }
    public string? Detail { get; set; }

    public bool IsActive => State is InstallState.Queued or InstallState.Running;

    public InstallRequest ToPublic()
        => new(Id, AppId, AppName, DeviceName, RequestedAt, CompletedAt, State, PercentComplete, Detail);
}

/// <summary>Install history, persisted as one JSON file under the data directory.</summary>
public sealed class InstallStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<InstallRecord>? _records;

    public InstallStore(IOptions<PortalOptions> options, IHostEnvironment env)
        : this(Path.Combine(Path.IsPathRooted(options.Value.DataDirectory) ? options.Value.DataDirectory : Path.Combine(env.ContentRootPath, options.Value.DataDirectory), "installs.json"))
    {
    }

    public InstallStore(string path)
    {
        _path = path;
    }

    public IReadOnlyList<InstallRecord> All()
    {
        lock (_gate)
        {
            return Load().Select(Clone).ToList();
        }
    }

    public IReadOnlyList<InstallRecord> ForDevice(string deviceName)
    {
        lock (_gate)
        {
            return Load()
                .Where(r => string.Equals(r.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.RequestedAt)
                .Select(Clone)
                .ToList();
        }
    }

    public InstallRecord? Find(string id)
    {
        lock (_gate)
        {
            var record = Load().FirstOrDefault(r => r.Id == id);
            return record is null ? null : Clone(record);
        }
    }

    /// <summary>
    /// Writes a record, unless the caller is working from an older snapshot than what is stored.
    /// The background poller and any number of HTTP requests refresh the same install concurrently,
    /// each from its own snapshot and its own round trip to Action1. Without this guard a slow
    /// earlier call can land after a fast later one and push a finished install back to Running.
    /// Returns false when the write was dropped as stale.
    /// </summary>
    public bool Upsert(InstallRecord record)
    {
        lock (_gate)
        {
            var records = Load();
            var index = records.FindIndex(r => r.Id == record.Id);
            if (index < 0)
            {
                records.Add(Clone(record));
                Save(records);
                return true;
            }

            var stored = records[index];
            if (!stored.IsActive && record.IsActive)
            {
                return false;
            }

            if (stored.LastCheckedAt is { } storedChecked && record.LastCheckedAt is { } incoming && incoming < storedChecked)
            {
                return false;
            }

            records[index] = Clone(record);
            Save(records);
            return true;
        }
    }

    private List<InstallRecord> Load()
    {
        if (_records is not null)
        {
            return _records;
        }

        _records = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<InstallRecord>>(File.ReadAllText(_path), Json) ?? []
            : [];
        return _records;
    }

    private void Save(List<InstallRecord> records)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(records, Json));
        File.Move(temp, _path, overwrite: true);
        _records = records;
    }

    private static InstallRecord Clone(InstallRecord r) => new()
    {
        Id = r.Id,
        DeviceName = r.DeviceName,
        EndpointId = r.EndpointId,
        AppId = r.AppId,
        AppName = r.AppName,
        PackageId = r.PackageId,
        Version = r.Version,
        AutomationId = r.AutomationId,
        RequestedAt = r.RequestedAt,
        CompletedAt = r.CompletedAt,
        LastCheckedAt = r.LastCheckedAt,
        State = r.State,
        PercentComplete = r.PercentComplete,
        Detail = r.Detail,
    };
}
