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

/// <summary>
/// Sessions for both admin doors, held server-side so that signing out revokes immediately. A
/// self-contained token would stay good until it expired no matter what the server decided in between.
/// Only the SHA-256 of a token is stored, the same way device tokens are kept.
/// </summary>
public sealed class AdminSessionStore(Database database)
{
    public static readonly TimeSpan WebLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan ApiLifetime = TimeSpan.FromHours(8);

    /// <summary>Creates a session and returns the plaintext token, which is never stored and never shown again.</summary>
    public string Create(string adminId, AdminSessionKind kind)
    {
        var token = GenerateToken(kind);
        var now = DateTimeOffset.UtcNow;

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_sessions (token_hash, admin_id, kind, created_at, expires_at, last_used_at)
            VALUES (@hash, @admin, @kind, @created, @expires, @created);
            """;
        command.Parameters.AddWithValue("@hash", AdminStore.Hash(token));
        command.Parameters.AddWithValue("@admin", adminId);
        command.Parameters.AddWithValue("@kind", Name(kind));
        command.Parameters.AddWithValue("@created", SqlTime.From(now));
        command.Parameters.AddWithValue("@expires", SqlTime.From(now + (kind == AdminSessionKind.Web ? WebLifetime : ApiLifetime)));
        command.ExecuteNonQuery();
        return token;
    }

    /// <summary>
    /// The admin behind a token, or null when the token is unknown, expired, of the wrong kind, or belongs
    /// to an account that has since been disabled. A web session slides: using it pushes its expiry out.
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
            touch.CommandText = kind == AdminSessionKind.Web
                ? "UPDATE admin_sessions SET last_used_at = @at, expires_at = @slid WHERE token_hash = @hash;"
                : "UPDATE admin_sessions SET last_used_at = @at WHERE token_hash = @hash;";
            touch.Parameters.AddWithValue("@at", SqlTime.From(now));
            touch.Parameters.AddWithValue("@hash", hash);
            if (kind == AdminSessionKind.Web)
            {
                touch.Parameters.AddWithValue("@slid", SqlTime.From(now + WebLifetime));
            }

            touch.ExecuteNonQuery();
        }

        transaction.Commit();

        // Read the account last: an admin disabled mid-session stops being able to act straight away.
        var admin = new AdminStore(database).FindById(adminId);
        return admin is null || admin.Disabled ? null : admin;
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
