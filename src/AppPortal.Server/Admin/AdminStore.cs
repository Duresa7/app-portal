using System.Security.Cryptography;
using System.Text;

using AppPortal.Server.Data;

using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace AppPortal.Server.Admin;

/// <summary>One local administrator. Passwords are only ever held as a PBKDF2 hash.</summary>
public sealed class AdminRecord
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool Disabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>Raised when an account cannot be created or changed as asked.</summary>
public sealed class AdminRejectedException(string message) : Exception(message);

/// <summary>
/// Local administrator accounts. Deliberately not the ASP.NET Identity framework: only its password
/// hasher is borrowed, so accounts live in the same SQLite file as the rest of the portal and a company
/// needs no directory and no identity provider to run this.
/// </summary>
public sealed class AdminStore(Database database)
{
    /// <summary>The only password rule. Length beats composition, and the plan rules the rest out.</summary>
    public const int MinimumPasswordLength = 12;

    private static readonly PasswordHasher<AdminRecord> Hasher = new();

    public IReadOnlyList<AdminRecord> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " ORDER BY username;";
        return Read(command);
    }

    /// <summary>True when no account exists yet, which is what the first-run message in the log reports.</summary>
    public bool None()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM admins;";
        return Convert.ToInt32(command.ExecuteScalar()) == 0;
    }

    public AdminRecord? Find(string username)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE username = @name COLLATE NOCASE;";
        command.Parameters.AddWithValue("@name", username);
        return Read(command).FirstOrDefault();
    }

    public AdminRecord? FindById(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return Read(command).FirstOrDefault();
    }

    public AdminRecord Add(string username, string password)
    {
        Validate(username, password);
        if (Find(username) is not null)
        {
            throw new AdminRejectedException($"An administrator named '{username}' already exists.");
        }

        var record = new AdminRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = username.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        record.PasswordHash = Hasher.HashPassword(record, password);

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admins (id, username, password_hash, disabled, created_at, last_login_at)
            VALUES (@id, @username, @hash, 0, @created, NULL);
            """;
        command.Parameters.AddWithValue("@id", record.Id);
        command.Parameters.AddWithValue("@username", record.Username);
        command.Parameters.AddWithValue("@hash", record.PasswordHash);
        command.Parameters.AddWithValue("@created", SqlTime.From(record.CreatedAt));
        command.ExecuteNonQuery();
        return record;
    }

    public void SetPassword(string username, string password)
    {
        var record = Find(username) ?? throw new AdminRejectedException($"There is no administrator named '{username}'.");
        Validate(username, password);

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE admins SET password_hash = @hash WHERE id = @id;";
        command.Parameters.AddWithValue("@hash", Hasher.HashPassword(record, password));
        command.Parameters.AddWithValue("@id", record.Id);
        command.ExecuteNonQuery();
    }

    public void SetDisabled(string username, bool disabled)
    {
        var record = Find(username) ?? throw new AdminRejectedException($"There is no administrator named '{username}'.");
        if (disabled && OnlyEnabledAdminIs(record.Id))
        {
            throw new AdminRejectedException("This is the only administrator left enabled. Add another before disabling this one.");
        }

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE admins SET disabled = @disabled WHERE id = @id;";
        command.Parameters.AddWithValue("@disabled", disabled ? 1 : 0);
        command.Parameters.AddWithValue("@id", record.Id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The account if the password is right and the account is enabled, otherwise null. A missing account
    /// still costs a hash verification, so an attacker cannot tell a wrong name from a wrong password by
    /// how long the answer took.
    /// </summary>
    public AdminRecord? Verify(string username, string password)
    {
        var record = Find(username);
        if (record is null)
        {
            Hasher.VerifyHashedPassword(Decoy, DecoyHash, password ?? "");
            return null;
        }

        var result = Hasher.VerifyHashedPassword(record, record.PasswordHash, password ?? "");
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            SetPassword(record.Username, password!);
        }

        return record.Disabled ? null : record;
    }

    public void RecordLogin(string adminId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE admins SET last_login_at = @at WHERE id = @id;";
        command.Parameters.AddWithValue("@at", SqlTime.Now());
        command.Parameters.AddWithValue("@id", adminId);
        command.ExecuteNonQuery();
    }

    private bool OnlyEnabledAdminIs(string adminId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM admins WHERE disabled = 0 AND id <> @id;";
        command.Parameters.AddWithValue("@id", adminId);
        return Convert.ToInt32(command.ExecuteScalar()) == 0;
    }

    private static void Validate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new AdminRejectedException("A user name is required.");
        }

        if (username.Trim().Length > 64 || username.Any(char.IsControl))
        {
            throw new AdminRejectedException("A user name must be at most 64 characters and contain no control characters.");
        }

        if (password is null || password.Length < MinimumPasswordLength)
        {
            throw new AdminRejectedException($"A password must be at least {MinimumPasswordLength} characters.");
        }
    }

    // Verifying against a throwaway hash makes the "no such account" path cost what the real one costs.
    private static readonly AdminRecord Decoy = new() { Id = "decoy", Username = "decoy" };
    private static readonly string DecoyHash = Hasher.HashPassword(Decoy, "decoy password, never accepted");

    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private const string Select = "SELECT id, username, password_hash, disabled, created_at, last_login_at FROM admins";

    private static List<AdminRecord> Read(SqliteCommand command)
    {
        var admins = new List<AdminRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            admins.Add(new AdminRecord
            {
                Id = reader.GetString(0),
                Username = reader.GetString(1),
                PasswordHash = reader.GetString(2),
                Disabled = reader.GetInt64(3) != 0,
                CreatedAt = SqlTime.Parse(reader.GetString(4)),
                LastLoginAt = SqlTime.ParseOptional(reader.IsDBNull(5) ? null : reader.GetString(5)),
            });
        }

        return admins;
    }
}
