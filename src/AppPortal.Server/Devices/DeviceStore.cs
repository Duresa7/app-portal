using System.Security.Cryptography;
using System.Text;

using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Data;

using Microsoft.Data.Sqlite;

namespace AppPortal.Server.Devices;

public sealed class DeviceRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The Action1 endpoint this device maps to, or empty when it has none.</summary>
    public string EndpointId { get; set; } = "";
    public string TokenSha256 { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>True once an agent has enrolled on this PC. Written by M2-01; shown here now.</summary>
    public bool HasAgent { get; set; }

    /// <summary>'action1', 'agent', or null to follow the server's preference. Used from M3-05.</summary>
    public string? EnginePreference { get; set; }

    public string? AgentVersion { get; set; }

    public string? EnrolledWithKeyId { get; set; }

    /// <summary>
    /// The stable hardware id the PC enrolled with, or null for a device added by hand. Enrollment
    /// matches on this first, so a reimaged PC updates its own row rather than creating a second.
    /// </summary>
    public string? MachineId { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>True when the device has an Action1 endpoint to deploy through.</summary>
    public bool HasAction1 => !string.IsNullOrWhiteSpace(EndpointId);
}

public sealed class DevicesFile
{
    public List<DeviceRecord> Devices { get; set; } = [];
}

/// <summary>
/// What an enrollment did: the device as it now stands, the token it must authenticate with from here
/// on, and whether the row was already there. The token is plaintext and is never stored.
/// </summary>
public sealed record EnrollmentResult(DeviceRecord Device, string Token, bool Existing);

/// <summary>
/// Devices that may call the API, each with the SHA-256 of its bearer token. The plaintext token is shown once,
/// when the device is added, and is never stored.
/// </summary>
public sealed class DeviceStore(Database database)
{
    public IReadOnlyList<DeviceRecord> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " ORDER BY name;";
        return Read(command);
    }

    public DeviceRecord? Authenticate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = Hash(token);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE token_hash = @hash AND enabled = 1;";
        command.Parameters.AddWithValue("@hash", hash);
        var device = Read(command).FirstOrDefault();

        // The lookup already matched on a hash of the secret rather than the secret. Comparing in fixed
        // time as well costs nothing and keeps the guarantee if this ever reads more than one row.
        return device is not null && FixedTimeEquals(device.TokenSha256, hash) ? device : null;
    }

    /// <summary>Adds a device and returns its plaintext token. Replaces an existing device of the same name.</summary>
    public string Add(string name, string endpointId)
    {
        var token = GenerateToken();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        // Names are matched without regard to case, as they were when this lived in a JSON file.
        string? existing;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT id FROM devices WHERE name = @name COLLATE NOCASE;";
            find.Parameters.AddWithValue("@name", name);
            existing = find.ExecuteScalar() as string;
        }

        if (existing is not null)
        {
            EnsureEndpointCanChange(connection, transaction, existing, endpointId);
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = existing is null
                ? """
                  INSERT INTO devices (id, name, token_hash, enabled, action1_endpoint_id, has_agent, created_at)
                  VALUES (@id, @name, @hash, 1, @endpoint, 0, @now);
                  """
                // An existing device keeps its row, and with it its install history, and gets a new token.
                : """
                  UPDATE devices SET name = @name, token_hash = @hash, enabled = 1, action1_endpoint_id = @endpoint
                  WHERE id = @id;
                  """;
            command.Parameters.AddWithValue("@id", existing ?? NewId());
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@hash", Hash(token));
            command.Parameters.AddWithValue("@endpoint", string.IsNullOrEmpty(endpointId) ? DBNull.Value : endpointId);
            command.Parameters.AddWithValue("@now", SqlTime.Now());
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return token;
    }

    /// <summary>
    /// Turns a spent enrollment key into a device and its token. The key has already been validated and
    /// counted by the caller; this is the half that decides which row the PC owns.
    ///
    /// A PC is recognised by <paramref name="machineId"/> first, so a reinstall updates the row it
    /// already has. Failing that it is recognised by name, which is what a reimage looks like: a fresh
    /// hardware id under the name the fleet already knows. Only when neither matches is a row created,
    /// and names stay unique because taking one over is the same move <see cref="Add"/> makes.
    ///
    /// What the key does not speak for is left alone. An agent-only key does not clear an Action1
    /// endpoint, and no key ever clears <c>has_agent</c>: the agent is either installed on that PC or it
    /// is not, and a key is not evidence that it was removed.
    /// </summary>
    public EnrollmentResult Enroll(
        string machineId,
        string name,
        string? endpointId,
        bool grantAgent,
        string? agentVersion,
        string keyId)
    {
        var trimmedMachine = (machineId ?? "").Trim();
        var trimmedName = (name ?? "").Trim();
        if (trimmedMachine.Length == 0)
        {
            throw new DeviceRejectedException("A machine id is required to enroll.");
        }

        if (trimmedName.Length == 0)
        {
            throw new DeviceRejectedException("A device needs a name.");
        }

        var endpoint = string.IsNullOrWhiteSpace(endpointId) ? null : endpointId.Trim();
        var token = GenerateToken();
        var now = SqlTime.Now();

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var existing = FindIdBy(connection, transaction, "machine_id = @value", trimmedMachine)
                       ?? FindIdBy(connection, transaction, "name = @value COLLATE NOCASE", trimmedName);

        if (existing is not null && endpoint is not null)
        {
            EnsureEndpointCanChange(connection, transaction, existing, endpoint);
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = existing is null
                ? """
                  INSERT INTO devices (id, name, token_hash, enabled, action1_endpoint_id, has_agent,
                                       agent_version, enrolled_with_key_id, machine_id, last_seen_at, created_at)
                  VALUES (@id, @name, @hash, 1, @endpoint, @agent, @agentVersion, @key, @machine, @now, @now);
                  """
                // COALESCE rather than assignment: a key that says nothing about an endpoint or an agent
                // version must not erase what the device already reported. Nor is an enrollment allowed to
                // re-enable a device an administrator disabled; the endpoint refuses that case outright.
                : """
                  UPDATE devices
                  SET name = @name,
                      token_hash = @hash,
                      action1_endpoint_id = COALESCE(@endpoint, action1_endpoint_id),
                      has_agent = CASE WHEN @agent = 1 THEN 1 ELSE has_agent END,
                      agent_version = COALESCE(@agentVersion, agent_version),
                      enrolled_with_key_id = @key,
                      machine_id = @machine,
                      last_seen_at = @now
                  WHERE id = @id;
                  """;
            command.Parameters.AddWithValue("@id", existing ?? NewId());
            command.Parameters.AddWithValue("@name", trimmedName);
            command.Parameters.AddWithValue("@hash", Hash(token));
            command.Parameters.AddWithValue("@endpoint", (object?)endpoint ?? DBNull.Value);
            command.Parameters.AddWithValue("@agent", grantAgent ? 1 : 0);
            command.Parameters.AddWithValue("@agentVersion", string.IsNullOrWhiteSpace(agentVersion) ? DBNull.Value : agentVersion.Trim());
            command.Parameters.AddWithValue("@key", keyId);
            command.Parameters.AddWithValue("@machine", trimmedMachine);
            command.Parameters.AddWithValue("@now", now);
            command.ExecuteNonQuery();
        }

        var id = existing ?? FindIdBy(connection, transaction, "machine_id = @value", trimmedMachine)!;

        // A renamed device should read as it is called now everywhere it appears, the same as an
        // administrator renaming it on the device page does.
        using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = "UPDATE installs SET device_name = @name WHERE device_id = @id;";
            history.Parameters.AddWithValue("@name", trimmedName);
            history.Parameters.AddWithValue("@id", id);
            history.ExecuteNonQuery();
        }

        DeviceRecord device;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = Select + " WHERE id = @id;";
            read.Parameters.AddWithValue("@id", id);
            device = Read(read).Single();
        }

        transaction.Commit();
        return new EnrollmentResult(device, token, existing is not null);
    }

    private static string? FindIdBy(SqliteConnection connection, SqliteTransaction transaction, string where, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id FROM devices WHERE {where};";
        command.Parameters.AddWithValue("@value", value);
        return command.ExecuteScalar() as string;
    }

    public DeviceRecord? Find(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return Read(command).FirstOrDefault();
    }

    /// <summary>The device that enrolled from this hardware, or null when none has. Never matches on null.</summary>
    public DeviceRecord? FindByMachineId(string? machineId)
    {
        if (string.IsNullOrWhiteSpace(machineId))
        {
            return null;
        }

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE machine_id = @machine;";
        command.Parameters.AddWithValue("@machine", machineId.Trim());
        return Read(command).FirstOrDefault();
    }

    public DeviceRecord? FindByName(string name)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE name = @name COLLATE NOCASE;";
        command.Parameters.AddWithValue("@name", name);
        return Read(command).FirstOrDefault();
    }

    /// <summary>
    /// Devices whose name contains the search term, by name unless the query sorts otherwise. The
    /// fleet is read whole and narrowed here: a substring match on a few hundred names is not worth a
    /// LIKE, and the retired-device rules that make removal careful stay out of the read path.
    /// </summary>
    public Slice<DeviceRecord> List(SearchFilter filter, ListQuery query)
    {
        var orderBy = Sorts.OrderBy(query.Sort);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + orderBy + ";";
        var devices = Read(command);
        var matching = filter.IsEmpty ? devices : devices.Where(d => filter.Matches(d.Name)).ToList();
        return Slice.Of(matching, query);
    }

    /// <summary>By name is how a fleet is scanned; every other order is by request.</summary>
    private static readonly SortColumns Sorts = new(
        "name",
        ("name", "name"),
        ("created", "created_at"),
        ("lastseen", "last_seen_at"),
        ("enabled", "enabled"));

    /// <summary>
    /// Writes the fields an administrator can change. The token is not among them: rotating is its own
    /// operation because it hands back a secret that is shown once.
    /// </summary>
    public void Update(DeviceRecord device)
    {
        var name = (device.Name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new DeviceInvalidException("A device needs a name.");
        }

        // Following the server has no word of its own: it is the absence of a preference, stored as
        // null, so the message names the empty value rather than a keyword nothing here would accept.
        var engine = string.IsNullOrWhiteSpace(device.EnginePreference) ? null : device.EnginePreference.Trim().ToLowerInvariant();
        if (engine is not null and not "action1" and not "agent")
        {
            throw new DeviceInvalidException("The engine preference must be action1 or agent, or empty to follow the server.");
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        EnsureEndpointCanChange(connection, transaction, device.Id, device.EndpointId);

        using (var clash = connection.CreateCommand())
        {
            clash.Transaction = transaction;
            clash.CommandText = "SELECT EXISTS(SELECT 1 FROM devices WHERE name = @name COLLATE NOCASE AND id <> @id);";
            clash.Parameters.AddWithValue("@name", name);
            clash.Parameters.AddWithValue("@id", device.Id);
            if (Convert.ToInt64(clash.ExecuteScalar()) != 0)
            {
                throw new DeviceRejectedException($"Another device is already called '{name}'.");
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE devices
                SET name = @name, enabled = @enabled, action1_endpoint_id = @endpoint, engine_preference = @engine
                WHERE id = @id;
                """;
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@enabled", device.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("@endpoint", string.IsNullOrWhiteSpace(device.EndpointId) ? DBNull.Value : device.EndpointId.Trim());
            command.Parameters.AddWithValue("@engine", (object?)engine ?? DBNull.Value);
            command.Parameters.AddWithValue("@id", device.Id);
            command.ExecuteNonQuery();
        }

        // The history keeps the name it was made under unless the device is renamed, in which case the
        // whole record should read as the device is called now.
        using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = "UPDATE installs SET device_name = @name WHERE device_id = @id;";
            history.Parameters.AddWithValue("@name", name);
            history.Parameters.AddWithValue("@id", device.Id);
            history.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Issues a new token and forgets the old one, which stops working on the very next call because
    /// authentication matches the stored hash.
    /// </summary>
    public string? RotateToken(string id)
    {
        var token = GenerateToken();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET token_hash = @hash WHERE id = @id;";
        command.Parameters.AddWithValue("@hash", Hash(token));
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteNonQuery() == 1 ? token : null;
    }

    /// <summary>
    /// What the agent's heartbeat writes. It sets <c>has_agent</c> rather than testing it first: a
    /// heartbeat arriving at all is the proof that an agent is installed, whatever the row said before.
    /// </summary>
    public void RecordHeartbeat(string id, string agentVersion)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE devices SET agent_version = @version, last_seen_at = @now, has_agent = 1
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@version", agentVersion);
        command.Parameters.AddWithValue("@now", SqlTime.Now());
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records that the device called just now. The caller decides how often this is worth doing; every
    /// API request would be a write per request for a number nobody reads to the second.
    /// </summary>
    public void TouchLastSeen(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET last_seen_at = @now WHERE id = @id;";
        command.Parameters.AddWithValue("@now", SqlTime.Now());
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes a device by id, keeping its install history, which carries the device name of its own
    /// since migration 006. False when an install is still in flight: that one is going to report back,
    /// and there would be no device for the answer to belong to.
    /// </summary>
    public bool RemoveById(string id)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using (var active = connection.CreateCommand())
        {
            active.Transaction = transaction;
            active.CommandText = "SELECT COUNT(*) FROM installs WHERE device_id = @id AND state IN ('Queued', 'Running') COLLATE NOCASE;";
            active.Parameters.AddWithValue("@id", id);
            if (Convert.ToInt32(active.ExecuteScalar()) > 0)
            {
                return false;
            }
        }

        // Make sure the history can name the device before the device stops existing.
        using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = transaction;
            stamp.CommandText = """
                UPDATE installs
                SET device_name = COALESCE(NULLIF(device_name, ''), (SELECT name FROM devices WHERE id = @id), '')
                WHERE device_id = @id;
                """;
            stamp.Parameters.AddWithValue("@id", id);
            stamp.ExecuteNonQuery();
        }

        using (var requests = connection.CreateCommand())
        {
            requests.Transaction = transaction;
            requests.CommandText = "UPDATE app_requests SET device_name = (SELECT name FROM devices WHERE id = @id) WHERE device_id = @id;";
            requests.Parameters.AddWithValue("@id", id);
            requests.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM devices WHERE id = @id;";
            delete.Parameters.AddWithValue("@id", id);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    /// <summary>How many installs each device has to its name, for the list page.</summary>
    public IReadOnlyDictionary<string, int> InstallCounts()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        // Installs whose device has been removed have no device_id any more. They still belong to the
        // history, but to no device, so they are counted against none.
        command.CommandText = "SELECT device_id, COUNT(*) FROM installs WHERE device_id IS NOT NULL GROUP BY device_id;";
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[reader.GetString(0)] = (int)reader.GetInt64(1);
        }

        return counts;
    }

    /// <summary>
    /// Removes a device by name. False means there was no such device. Kept for the CLI, which knows
    /// devices by name; it refuses while an install is in flight, the same as the page does.
    /// </summary>
    public bool Remove(string name)
    {
        var device = FindByName(name);
        if (device is null)
        {
            return false;
        }

        // Settled history no longer blocks a removal: it carries the device name itself now, so it
        // survives on its own. Only an install still in flight does.
        if (!RemoveById(device.Id))
        {
            throw new DeviceInUseException($"'{device.Name}' has an install in progress and cannot be removed yet. Wait for it to finish, or disable the device.");
        }

        return true;
    }

    private static void EnsureEndpointCanChange(SqliteConnection connection, SqliteTransaction transaction, string id, string? endpointId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM devices d
                WHERE d.id = @id AND COALESCE(d.action1_endpoint_id, '') <> @endpoint
                  AND EXISTS(SELECT 1 FROM installs i WHERE i.device_id = d.id AND i.state IN ('Queued', 'Running')));
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@endpoint", endpointId?.Trim() ?? "");
        if (Convert.ToInt64(command.ExecuteScalar()) != 0)
        {
            throw new DeviceRejectedException("The Action1 endpoint cannot change while an install is in progress.");
        }
    }

    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewId() => Guid.NewGuid().ToString("N");

    private const string Select = """
        SELECT id, name, token_hash, enabled, action1_endpoint_id, created_at,
               has_agent, engine_preference, agent_version, enrolled_with_key_id, last_seen_at, machine_id
        FROM devices
        """;

    private static List<DeviceRecord> Read(SqliteCommand command)
    {
        var devices = new List<DeviceRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            devices.Add(new DeviceRecord
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                TokenSha256 = reader.GetString(2),
                Enabled = reader.GetInt64(3) != 0,
                EndpointId = reader.IsDBNull(4) ? "" : reader.GetString(4),
                CreatedAt = SqlTime.Parse(reader.GetString(5)),
                HasAgent = reader.GetInt64(6) != 0,
                EnginePreference = reader.IsDBNull(7) ? null : reader.GetString(7),
                AgentVersion = reader.IsDBNull(8) ? null : reader.GetString(8),
                EnrolledWithKeyId = reader.IsDBNull(9) ? null : reader.GetString(9),
                LastSeenAt = SqlTime.ParseOptional(reader.IsDBNull(10) ? null : reader.GetString(10)),
                MachineId = reader.IsDBNull(11) ? null : reader.GetString(11),
            });
        }

        return devices;
    }

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
}

/// <summary>Raised when a device cannot be removed because an install on it is still running.</summary>
public sealed class DeviceInUseException(string message) : Exception(message);

/// <summary>Raised when a change to a device is not allowed, with wording meant for an administrator.</summary>
public class DeviceRejectedException(string message) : Exception(message);

/// <summary>
/// Raised when a change to a device is refused for what was sent rather than for what it met: a blank
/// name, an engine preference that names no engine. It is still a rejection, so a caller that only shows
/// the reason can catch the base type; the API tells the two apart because bad input is 400 and a
/// clash with the fleet is 409.
/// </summary>
public sealed class DeviceInvalidException(string message) : DeviceRejectedException(message);
