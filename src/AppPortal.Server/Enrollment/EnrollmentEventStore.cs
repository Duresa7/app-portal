using AppPortal.Server.Data;

using Microsoft.Data.Sqlite;

namespace AppPortal.Server.Enrollment;

/// <summary>What became of one enrollment attempt.</summary>
public enum EnrollmentOutcome
{
    /// <summary>A device row was created.</summary>
    Enrolled,

    /// <summary>A PC the portal already knew took a new token, under the row it already owned.</summary>
    ReEnrolled,

    /// <summary>The key was unknown, expired, used up or revoked. Which one is not said, on purpose.</summary>
    KeyRefused,

    /// <summary>The key was good but the body was not: a missing field, or no endpoint where one is required.</summary>
    Rejected,

    /// <summary>The key was good and the PC is known, but an administrator has disabled that device.</summary>
    DeviceDisabled,
}

/// <summary>One line of the enrollment audit trail, as the key detail page reads it.</summary>
public sealed record EnrollmentEventRecord(
    string Id,
    string? KeyId,
    string? DeviceId,
    string? DeviceName,
    string Source,
    EnrollmentOutcome Outcome,
    DateTimeOffset CreatedAt)
{
    /// <summary>Wording for an administrator, rather than the name the column stores.</summary>
    public string Describe => Outcome switch
    {
        EnrollmentOutcome.Enrolled => "Enrolled",
        EnrollmentOutcome.ReEnrolled => "Re-enrolled",
        EnrollmentOutcome.KeyRefused => "Refused: the key was not usable",
        EnrollmentOutcome.Rejected => "Refused: the request was incomplete",
        EnrollmentOutcome.DeviceDisabled => "Refused: the device is disabled",
        _ => Outcome.ToString(),
    };

    /// <summary>True for the two outcomes that ended in a device holding a token.</summary>
    public bool Succeeded => Outcome is EnrollmentOutcome.Enrolled or EnrollmentOutcome.ReEnrolled;
}

/// <summary>
/// Every enrollment attempt, accepted or refused, so an administrator reading a key can see what it has
/// let in and from where. Nothing secret is written here: the key is named by its row, never by its
/// plaintext, and the token that an enrollment hands back is not recorded at all.
/// </summary>
public sealed class EnrollmentEventStore(Database database)
{
    /// <summary>How many events the key detail page shows before it stops.</summary>
    public const int PageSize = 50;

    public void Record(string? keyId, string? deviceId, string source, EnrollmentOutcome outcome)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO enrollment_events (id, key_id, device_id, source, outcome, created_at)
            VALUES (@id, @key, @device, @source, @outcome, @now);
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@key", (object?)keyId ?? DBNull.Value);
        command.Parameters.AddWithValue("@device", (object?)deviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@source", string.IsNullOrWhiteSpace(source) ? "unknown" : source);
        command.Parameters.AddWithValue("@outcome", Name(outcome));
        command.Parameters.AddWithValue("@now", SqlTime.Now());
        command.ExecuteNonQuery();
    }

    /// <summary>The attempts made with one key, newest first.</summary>
    public IReadOnlyList<EnrollmentEventRecord> ForKey(string keyId, int limit = PageSize)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        // Left join: an event outlives the device it created, and an administrator removing a device
        // should not blank the line that records where it came from.
        command.CommandText = """
            SELECT e.id, e.key_id, e.device_id, d.name, e.source, e.outcome, e.created_at
            FROM enrollment_events e
            LEFT JOIN devices d ON d.id = e.device_id
            WHERE e.key_id = @key
            ORDER BY e.created_at DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@key", keyId);
        command.Parameters.AddWithValue("@limit", limit);
        return Read(command);
    }

    /// <summary>How many attempts each key has to its name, for the key list.</summary>
    public IReadOnlyDictionary<string, int> CountsByKey()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key_id, COUNT(*) FROM enrollment_events WHERE key_id IS NOT NULL GROUP BY key_id;";
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[reader.GetString(0)] = (int)reader.GetInt64(1);
        }

        return counts;
    }

    /// <summary>The names the column stores. Hyphenated so they read as they are, not as C# spells them.</summary>
    public static string Name(EnrollmentOutcome outcome) => outcome switch
    {
        EnrollmentOutcome.Enrolled => "enrolled",
        EnrollmentOutcome.ReEnrolled => "re-enrolled",
        EnrollmentOutcome.KeyRefused => "key-refused",
        EnrollmentOutcome.Rejected => "rejected",
        EnrollmentOutcome.DeviceDisabled => "device-disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "No such enrollment outcome."),
    };

    private static EnrollmentOutcome Parse(string text) => text switch
    {
        "enrolled" => EnrollmentOutcome.Enrolled,
        "re-enrolled" => EnrollmentOutcome.ReEnrolled,
        "key-refused" => EnrollmentOutcome.KeyRefused,
        "device-disabled" => EnrollmentOutcome.DeviceDisabled,
        // Anything a later version wrote and this one does not know reads as a plain refusal rather
        // than throwing: an audit page that cannot render is worse than one line it cannot name.
        _ => EnrollmentOutcome.Rejected,
    };

    private static List<EnrollmentEventRecord> Read(SqliteCommand command)
    {
        var events = new List<EnrollmentEventRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add(new EnrollmentEventRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                Parse(reader.GetString(5)),
                SqlTime.Parse(reader.GetString(6))));
        }

        return events;
    }
}
