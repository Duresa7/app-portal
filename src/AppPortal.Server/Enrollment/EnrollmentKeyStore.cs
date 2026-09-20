using System.Security.Cryptography;
using System.Text;

using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Data;

using Microsoft.Data.Sqlite;

namespace AppPortal.Server.Enrollment;

/// <summary>Which install engines a device enrolled with this key is expected to have.</summary>
public enum EnrollmentEngine
{
    Action1,
    Agent,
    Both,
}

/// <summary>Why a key will not be accepted, or <see cref="Active"/> when it still will be.</summary>
public enum EnrollmentKeyStatus
{
    Active,
    Expired,
    Exhausted,
    Revoked,
}

/// <summary>One enrollment key, without the secret. The plaintext exists only in the reply that created it.</summary>
public sealed class EnrollmentKeyRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The first eight characters after <c>ape_</c>, enough to tell two rows apart.</summary>
    public string KeyPrefix { get; set; } = "";
    public EnrollmentEngine DefaultEngine { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public int Uses { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Revocation outranks the rest: an administrator who revoked a key wants to read "Revoked",
    /// not "Expired", however long it has since sat there.
    /// </summary>
    public EnrollmentKeyStatus Status
    {
        get
        {
            if (RevokedAt is not null)
            {
                return EnrollmentKeyStatus.Revoked;
            }

            if (ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
            {
                return EnrollmentKeyStatus.Expired;
            }

            return MaxUses is { } max && Uses >= max ? EnrollmentKeyStatus.Exhausted : EnrollmentKeyStatus.Active;
        }
    }

    public string DisplayPrefix => EnrollmentKeyStore.Prefix + KeyPrefix;

    /// <summary>"3 of 10", or "3" when the key has no limit.</summary>
    public string UsesText => MaxUses is { } max ? $"{Uses} of {max}" : Uses.ToString();
}

/// <summary>A new key and its plaintext. The plaintext is not stored and cannot be shown again.</summary>
public sealed record EnrollmentKeyCreated(EnrollmentKeyRecord Key, string Plaintext);

/// <summary>Raised when a key cannot be created as asked.</summary>
public sealed class EnrollmentKeyRejectedException(string message) : Exception(message);

/// <summary>
/// Enrollment keys, one row each. A key is shown once and stored as a SHA-256, the same bargain the
/// device token makes: a leaked database gives an attacker hashes, not a way onto the portal.
/// </summary>
public sealed class EnrollmentKeyStore(Database database)
{
    public const string Prefix = "ape_";

    /// <summary>Thirty-two characters of Crockford-free RFC 4648 base32: unambiguous when read aloud or retyped.</summary>
    public const int SecretLength = 32;

    private const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Every key, newest first. What the command line prints and the device list names keys by.</summary>
    public IReadOnlyList<EnrollmentKeyRecord> List() => List(NoFilter.Instance, ListQuery.All).Rows;

    /// <summary>The keys, newest first unless the query sorts otherwise. Nothing narrows this list.</summary>
    public Slice<EnrollmentKeyRecord> List(NoFilter filter, ListQuery query)
    {
        var orderBy = Sorts.OrderBy(query.Sort);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + orderBy + ";";
        return Slice.Of(Read(command), query);
    }

    /// <summary>Newest first, so the key just created is at the top; every other order is by request.</summary>
    private static readonly SortColumns Sorts = new(
        "created_at DESC",
        ("name", "name"),
        ("created", "created_at"),
        ("expires", "expires_at"),
        ("uses", "uses"));

    public EnrollmentKeyRecord? Find(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return Read(command).FirstOrDefault();
    }

    public EnrollmentKeyCreated Create(
        string name,
        EnrollmentEngine defaultEngine,
        DateTimeOffset? expiresAt,
        int? maxUses,
        string createdBy)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
        {
            throw new EnrollmentKeyRejectedException("Give the key a name, so it can be told apart later.");
        }

        if (maxUses is { } max && max < 1)
        {
            throw new EnrollmentKeyRejectedException("A key that may be used fewer than once cannot enroll anything.");
        }

        if (expiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            throw new EnrollmentKeyRejectedException("That expiry has already passed.");
        }

        var plaintext = Generate();
        var record = new EnrollmentKeyRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = trimmed,
            KeyPrefix = plaintext[Prefix.Length..(Prefix.Length + 8)],
            DefaultEngine = defaultEngine,
            ExpiresAt = expiresAt,
            MaxUses = maxUses,
            Uses = 0,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "unknown" : createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO enrollment_keys (id, name, key_hash, key_prefix, default_engine, expires_at, max_uses, uses, revoked_at, created_by, created_at)
            VALUES (@id, @name, @hash, @prefix, @engine, @expires, @max, 0, NULL, @by, @created);
            """;
        command.Parameters.AddWithValue("@id", record.Id);
        command.Parameters.AddWithValue("@name", record.Name);
        command.Parameters.AddWithValue("@hash", Hash(plaintext));
        command.Parameters.AddWithValue("@prefix", record.KeyPrefix);
        command.Parameters.AddWithValue("@engine", Name(record.DefaultEngine));
        command.Parameters.AddWithValue("@expires", (object?)SqlTime.FromOptional(record.ExpiresAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("@max", (object?)record.MaxUses ?? DBNull.Value);
        command.Parameters.AddWithValue("@by", record.CreatedBy);
        command.Parameters.AddWithValue("@created", SqlTime.From(record.CreatedAt));
        command.ExecuteNonQuery();

        return new EnrollmentKeyCreated(record, plaintext);
    }

    /// <summary>Stops a key being spent again. False when there is no such key or it was already revoked.</summary>
    public bool Revoke(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE enrollment_keys SET revoked_at = @at WHERE id = @id AND revoked_at IS NULL;";
        command.Parameters.AddWithValue("@at", SqlTime.Now());
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Spends one use of a key, or answers null when it is unknown, revoked, expired or used up.
    /// M2-01 calls this to enroll a device.
    ///
    /// The check and the increment are one UPDATE on purpose. Reading the row, deciding, then writing
    /// would let two machines racing for the last use of a key both read <c>uses = 0</c> and both win;
    /// here SQLite's write lock serialises them and the second one's WHERE clause no longer matches.
    /// </summary>
    public EnrollmentKeyRecord? TryConsume(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return null;
        }

        var hash = Hash(plaintext.Trim());
        using var connection = database.Open();

        // The transaction opens with the write, so no read snapshot is taken that would have to be
        // upgraded; a competing caller waits for the lock rather than failing to get one.
        using var transaction = connection.BeginTransaction();
        using (var spend = connection.CreateCommand())
        {
            spend.Transaction = transaction;
            spend.CommandText = """
                UPDATE enrollment_keys SET uses = uses + 1
                WHERE key_hash = @hash
                  AND revoked_at IS NULL
                  AND (expires_at IS NULL OR expires_at > @now)
                  AND (max_uses IS NULL OR uses < max_uses);
                """;
            spend.Parameters.AddWithValue("@hash", hash);
            spend.Parameters.AddWithValue("@now", SqlTime.Now());
            if (spend.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = Select + " WHERE key_hash = @hash;";
        read.Parameters.AddWithValue("@hash", hash);
        var record = Read(read).FirstOrDefault();
        transaction.Commit();
        return record;
    }

    public static string Hash(string key)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string Name(EnrollmentEngine engine) => engine.ToString().ToLowerInvariant();

    /// <summary>Accepts the three names the column stores; anything else is refused rather than guessed at.</summary>
    public static EnrollmentEngine ParseEngine(string? text)
        => Enum.TryParse<EnrollmentEngine>((text ?? "").Trim(), ignoreCase: true, out var engine) && Enum.IsDefined(engine)
            ? engine
            : throw new EnrollmentKeyRejectedException("The engine must be action1, agent or both.");

    private static string Generate()
    {
        // One uniform byte per character, masked to five bits. Thirty-two divides 256 exactly, so no
        // character is likelier than another the way a plain modulo of a larger alphabet would make it.
        var bytes = RandomNumberGenerator.GetBytes(SecretLength);
        var chars = new char[SecretLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Base32[bytes[i] & 31];
        }

        return Prefix + new string(chars);
    }

    private const string Select = """
        SELECT id, name, key_prefix, default_engine, expires_at, max_uses, uses, revoked_at, created_by, created_at
        FROM enrollment_keys
        """;

    private static List<EnrollmentKeyRecord> Read(SqliteCommand command)
    {
        var keys = new List<EnrollmentKeyRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            keys.Add(new EnrollmentKeyRecord
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                KeyPrefix = reader.GetString(2),
                DefaultEngine = Enum.TryParse<EnrollmentEngine>(reader.GetString(3), ignoreCase: true, out var engine)
                    ? engine
                    : EnrollmentEngine.Action1,
                ExpiresAt = SqlTime.ParseOptional(reader.IsDBNull(4) ? null : reader.GetString(4)),
                MaxUses = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Uses = reader.GetInt32(6),
                RevokedAt = SqlTime.ParseOptional(reader.IsDBNull(7) ? null : reader.GetString(7)),
                CreatedBy = reader.GetString(8),
                CreatedAt = SqlTime.Parse(reader.GetString(9)),
            });
        }

        return keys;
    }
}
