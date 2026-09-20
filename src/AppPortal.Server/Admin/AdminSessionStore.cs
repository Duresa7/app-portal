using System.Security.Cryptography;

using AppPortal.Server.Data;

namespace AppPortal.Server.Admin;

/// <summary>Which door a session came through. The two never substitute for one another.</summary>
public enum AdminSessionKind
{
    /// <summary>The cookie the admin pages in a browser carry.</summary>
    Web,

    /// <summary>An <c>apa_</c> bearer token, used by the Windows client's admin mode from M4-01.</summary>
    Api,
}

/// <summary>One session an account holds, without anything derived from its token.</summary>
public sealed record AdminSessionRecord(
    string Id,
    string AdminId,
    string? DeviceName,
    AdminSessionKind Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt);

/// <summary>
/// Sessions for both admin doors, held server-side so that signing out revokes immediately. A
/// self-contained token would stay good until it expired no matter what the server decided in between.
/// Only the SHA-256 of a token is stored, the same way device tokens are kept.
/// </summary>
public sealed class AdminSessionStore(Database database)
{
    public static readonly TimeSpan WebLifetime = TimeSpan.FromHours(8);

    /// <summary>
    /// Thirty days, sliding, for the Windows client. A person administering a fleet from the client all
    /// week should not be asked for a password every eight hours, and the session is revocable at any
    /// moment from the sessions list, which is the protection an unrevocable long-lived token lacks.
    /// </summary>
    public static readonly TimeSpan ApiLifetime = TimeSpan.FromDays(30);

    /// <summary>The longest device name a session records; a client cannot fill the column with prose.</summary>
    public const int MaxDeviceNameLength = 64;

    public static TimeSpan LifetimeOf(AdminSessionKind kind) => kind == AdminSessionKind.Web ? WebLifetime : ApiLifetime;

    /// <summary>Creates a session and returns the plaintext token, which is never stored and never shown again.</summary>
    public string Create(string adminId, AdminSessionKind kind, string? deviceName = null)
    {
        var token = GenerateToken(kind);
        var now = DateTimeOffset.UtcNow;

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_sessions (id, token_hash, admin_id, kind, device_name, created_at, expires_at, last_used_at)
            VALUES (@id, @hash, @admin, @kind, @device, @created, @expires, @created);
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@hash", AdminStore.Hash(token));
        command.Parameters.AddWithValue("@admin", adminId);
        command.Parameters.AddWithValue("@kind", Name(kind));
        command.Parameters.AddWithValue("@device", (object?)Trim(deviceName) ?? DBNull.Value);
        command.Parameters.AddWithValue("@created", SqlTime.From(now));
        command.Parameters.AddWithValue("@expires", SqlTime.From(now + LifetimeOf(kind)));
        command.ExecuteNonQuery();
        return token;
    }

    /// <summary>
    /// Blank is no name at all, and a long one is cut rather than refused: it is only a label. Control
    /// characters go, because this string is written by a caller and read back by other clients.
    /// </summary>
    private static string? Trim(string? deviceName)
    {
        var trimmed = new string([.. (deviceName ?? "").Where(c => !char.IsControl(c))]).Trim();
        return trimmed.Length == 0
            ? null
            : trimmed[..Math.Min(trimmed.Length, MaxDeviceNameLength)];
    }

    /// <summary>
    /// The admin behind a token, or null when the token is unknown, expired, of the wrong kind, or belongs
    /// to an account that has since been disabled. Both kinds slide: using a session pushes its expiry out,
    /// so a client in daily use stays signed in and one that has been put away stops working on its own.
    /// </summary>
    public AdminRecord? Resolve(string? token, AdminSessionKind kind)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = AdminStore.Hash(token);
        var now = DateTimeOffset.UtcNow;

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        string adminId;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT admin_id, expires_at FROM admin_sessions WHERE token_hash = @hash AND kind = @kind;";
            read.Parameters.AddWithValue("@hash", hash);
            read.Parameters.AddWithValue("@kind", Name(kind));
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            if (SqlTime.Parse(reader.GetString(1)) <= now)
            {
                return null;
            }

            adminId = reader.GetString(0);
        }

        using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE admin_sessions SET last_used_at = @at, expires_at = @slid WHERE token_hash = @hash;";
            touch.Parameters.AddWithValue("@at", SqlTime.From(now));
            touch.Parameters.AddWithValue("@slid", SqlTime.From(now + LifetimeOf(kind)));
            touch.Parameters.AddWithValue("@hash", hash);
            touch.ExecuteNonQuery();
        }

        transaction.Commit();

        // Read the account last: an admin disabled mid-session stops being able to act straight away.
        var admin = new AdminStore(database).FindById(adminId);
        return admin is null || admin.Disabled ? null : admin;
    }

    /// <summary>
    /// Every session one account holds, newest first, expired ones included: an administrator reading
    /// the list wants to see the sessions that exist, and hiding a stale row would only make the list
    /// disagree with what a revocation does.
    /// </summary>
    public IReadOnlyList<AdminSessionRecord> ListFor(string adminId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, admin_id, device_name, kind, created_at, expires_at, last_used_at
            FROM admin_sessions WHERE admin_id = @admin ORDER BY created_at DESC;
            """;
        command.Parameters.AddWithValue("@admin", adminId);

        var sessions = new List<AdminSessionRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new AdminSessionRecord(
                reader.IsDBNull(0) ? "" : reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3) == "web" ? AdminSessionKind.Web : AdminSessionKind.Api,
                SqlTime.Parse(reader.GetString(4)),
                SqlTime.Parse(reader.GetString(5)),
                SqlTime.ParseOptional(reader.IsDBNull(6) ? null : reader.GetString(6))));
        }

        return sessions;
    }

    /// <summary>
    /// The id of the session a token belongs to, so the caller can tell its own row apart in the list
    /// without the id ever having to travel beside the token.
    /// </summary>
    public string? IdOf(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM admin_sessions WHERE token_hash = @hash;";
        command.Parameters.AddWithValue("@hash", AdminStore.Hash(token));
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Revokes one session of one account. The account is part of the match on purpose: an id is not a
    /// secret, and nobody should be able to sign another administrator out by guessing one.
    /// </summary>
    public bool RevokeById(string adminId, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM admin_sessions WHERE id = @id AND admin_id = @admin;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@admin", adminId);
        return command.ExecuteNonQuery() == 1;
    }

    public void Revoke(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM admin_sessions WHERE token_hash = @hash;";
        command.Parameters.AddWithValue("@hash", AdminStore.Hash(token));
        command.ExecuteNonQuery();
    }

    /// <summary>Drops every session an account holds, which is what a password change has to do.</summary>
    public void RevokeAllFor(string adminId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM admin_sessions WHERE admin_id = @admin;";
        command.Parameters.AddWithValue("@admin", adminId);
        command.ExecuteNonQuery();
    }

    public int DeleteExpired()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM admin_sessions WHERE expires_at <= @now;";
        command.Parameters.AddWithValue("@now", SqlTime.Now());
        return command.ExecuteNonQuery();
    }

    private static string Name(AdminSessionKind kind) => kind == AdminSessionKind.Web ? "web" : "api";

    /// <summary>
    /// <c>apa_</c> for the bearer token the roadmap names, <c>apw_</c> for the cookie's value, which is
    /// this project's own and never leaves the browser.
    /// </summary>
    private static string GenerateToken(AdminSessionKind kind)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var prefix = kind == AdminSessionKind.Api ? "apa_" : "apw_";
        return prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
