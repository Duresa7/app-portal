using AppPortal.Server.Enrollment;

using Microsoft.Data.Sqlite;

namespace AppPortal.Server.Tests;

public sealed class EnrollmentKeyStoreTests : IDisposable
{
    private readonly TestDatabase _test = new();
    private readonly EnrollmentKeyStore _keys;

    public EnrollmentKeyStoreTests() => _keys = new EnrollmentKeyStore(_test.Database);

    [Fact]
    public void A_new_key_is_shown_once_and_stored_only_as_a_hash()
    {
        var created = _keys.Create("Sales laptops", EnrollmentEngine.Both, null, null, "admin");

        Assert.StartsWith("ape_", created.Plaintext, StringComparison.Ordinal);
        Assert.Equal(EnrollmentKeyStore.Prefix.Length + EnrollmentKeyStore.SecretLength, created.Plaintext.Length);
        Assert.All(created.Plaintext[EnrollmentKeyStore.Prefix.Length..], c => Assert.True(
            (c >= 'A' && c <= 'Z') || (c >= '2' && c <= '7'),
            $"'{c}' is not an RFC 4648 base32 character."));

        // The row must hold the hash and the display prefix, and the plaintext must appear in no column.
        using var connection = _test.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, key_hash, key_prefix, default_engine, created_by FROM enrollment_keys;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(EnrollmentKeyStore.Hash(created.Plaintext), reader.GetString(2));
        Assert.Equal(created.Plaintext[4..12], reader.GetString(3));
        Assert.Equal("both", reader.GetString(4));
        Assert.Equal("admin", reader.GetString(5));
        for (var i = 0; i < reader.FieldCount; i++)
        {
            Assert.DoesNotContain(created.Plaintext, reader.GetString(i), StringComparison.Ordinal);
        }

        Assert.False(reader.Read());
    }

    [Fact]
    public void Two_keys_never_share_a_secret()
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 50; i++)
        {
            Assert.True(secrets.Add(_keys.Create($"key {i}", EnrollmentEngine.Action1, null, null, "admin").Plaintext));
        }
    }

    [Fact]
    public void A_key_is_spent_one_use_at_a_time()
    {
        var created = _keys.Create("Two uses", EnrollmentEngine.Action1, null, 2, "admin");

        Assert.NotNull(_keys.TryConsume(created.Plaintext));
        Assert.Equal(1, _keys.Find(created.Key.Id)!.Uses);

        var second = _keys.TryConsume(created.Plaintext);
        Assert.NotNull(second);
        Assert.Equal(2, second!.Uses);
        Assert.Equal(EnrollmentKeyStatus.Exhausted, second.Status);

        Assert.Null(_keys.TryConsume(created.Plaintext));
        Assert.Equal(2, _keys.Find(created.Key.Id)!.Uses);
    }

    [Fact]
    public void An_unlimited_key_keeps_being_spent()
    {
        var created = _keys.Create("Unlimited", EnrollmentEngine.Agent, null, null, "admin");

        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(_keys.TryConsume(created.Plaintext));
        }

        var key = _keys.Find(created.Key.Id)!;
        Assert.Equal(5, key.Uses);
        Assert.Equal(EnrollmentKeyStatus.Active, key.Status);
        Assert.Equal("5", key.UsesText);
    }

    [Fact]
    public void A_revoked_key_is_refused()
    {
        var created = _keys.Create("Revoked", EnrollmentEngine.Action1, null, null, "admin");

        Assert.True(_keys.Revoke(created.Key.Id));
        Assert.Null(_keys.TryConsume(created.Plaintext));
        Assert.Equal(EnrollmentKeyStatus.Revoked, _keys.Find(created.Key.Id)!.Status);

        // Revoking twice is not an error the caller has to handle, but it is not a second revocation either.
        Assert.False(_keys.Revoke(created.Key.Id));
    }

    [Fact]
    public void An_expired_key_is_refused()
    {
        var created = _keys.Create("Expires soon", EnrollmentEngine.Action1, DateTimeOffset.UtcNow.AddHours(1), null, "admin");
        Assert.NotNull(_keys.TryConsume(created.Plaintext));

        // Moved into the past behind the store's back, because Create rightly refuses a past expiry.
        Expire(created.Key.Id, DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Null(_keys.TryConsume(created.Plaintext));
        Assert.Equal(EnrollmentKeyStatus.Expired, _keys.Find(created.Key.Id)!.Status);
    }

    [Fact]
    public void An_unknown_or_empty_key_is_refused_without_a_row_to_match()
    {
        _keys.Create("Real", EnrollmentEngine.Action1, null, null, "admin");

        Assert.Null(_keys.TryConsume("ape_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        Assert.Null(_keys.TryConsume(""));
        Assert.Null(_keys.TryConsume("   "));
    }

    [Fact]
    public void Creating_a_key_refuses_what_it_cannot_honour()
    {
        Assert.Throws<EnrollmentKeyRejectedException>(() => _keys.Create("  ", EnrollmentEngine.Action1, null, null, "admin"));
        Assert.Throws<EnrollmentKeyRejectedException>(() => _keys.Create("Zero", EnrollmentEngine.Action1, null, 0, "admin"));
        Assert.Throws<EnrollmentKeyRejectedException>(() => _keys.Create("Past", EnrollmentEngine.Action1, DateTimeOffset.UtcNow.AddMinutes(-1), null, "admin"));
        Assert.Throws<EnrollmentKeyRejectedException>(() => EnrollmentKeyStore.ParseEngine("winget"));
    }

    [Fact]
    public async Task The_last_use_of_a_key_goes_to_exactly_one_of_twenty_racing_callers()
    {
        var created = _keys.Create("One use only", EnrollmentEngine.Action1, null, 1, "admin");

        // Every caller is released at once, so they contend for the same final use rather than queueing
        // politely behind each other the way a sequential loop would.
        using var start = new ManualResetEventSlim(false);
        var winners = 0;
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            start.Wait();
            if (_keys.TryConsume(created.Plaintext) is not null)
            {
                Interlocked.Increment(ref winners);
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(1, winners);
        Assert.Equal(1, _keys.Find(created.Key.Id)!.Uses);
    }

    [Fact]
    public void Keys_are_listed_newest_first()
    {
        var first = _keys.Create("First", EnrollmentEngine.Action1, null, null, "admin");
        Thread.Sleep(10);
        var second = _keys.Create("Second", EnrollmentEngine.Agent, null, null, "admin");

        var listed = _keys.List();

        Assert.Equal(2, listed.Count);
        Assert.Equal(second.Key.Id, listed[0].Id);
        Assert.Equal(first.Key.Id, listed[1].Id);
    }

    private void Expire(string id, DateTimeOffset at)
    {
        using var connection = _test.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE enrollment_keys SET expires_at = @at WHERE id = @id;";
        command.Parameters.AddWithValue("@at", AppPortal.Server.Data.SqlTime.From(at));
        command.Parameters.AddWithValue("@id", id);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _test.Dispose();
    }
}
