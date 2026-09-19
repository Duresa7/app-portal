using System.Security.Cryptography;
using System.Text;

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
}

public sealed class DevicesFile
{
    public List<DeviceRecord> Devices { get; set; } = [];
}

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
    /// Removes a device. False means there was no such device. A device with install history cannot be
    /// removed: the history points at it, and until M1-09 stores the device name alongside each install,
    /// removing the device would take the history with it.
    /// </summary>
    public bool Remove(string name)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        string id;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT id FROM devices WHERE name = @name COLLATE NOCASE;";
            find.Parameters.AddWithValue("@name", name);
            if (find.ExecuteScalar() is not string found)
            {
                return false;
            }

            id = found;
        }

        using (var installs = connection.CreateCommand())
        {
            installs.Transaction = transaction;
            installs.CommandText = "SELECT COUNT(*) FROM installs WHERE device_id = @id;";
            installs.Parameters.AddWithValue("@id", id);
            var count = Convert.ToInt32(installs.ExecuteScalar());
            if (count > 0)
            {
                throw new DeviceInUseException($"'{name}' has {count} install(s) in its history and cannot be removed. Disable it instead.");
            }
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

    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewId() => Guid.NewGuid().ToString("N");

    private const string Select = "SELECT id, name, token_hash, enabled, action1_endpoint_id, created_at FROM devices";

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

/// <summary>Raised when a device cannot be removed because its install history refers to it.</summary>
public sealed class DeviceInUseException(string message) : Exception(message);
