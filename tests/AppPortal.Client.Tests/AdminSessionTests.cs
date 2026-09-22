using System.Net;
using System.Text;

using AppPortal.Client.Services;

namespace AppPortal.Client.Tests;

public sealed class AdminSessionTests
{
    private const string Server = "https://portal.example";

    [Fact]
    public async Task A_sign_in_is_still_there_after_a_restart()
    {
        var store = new MemoryStore();
        var first = new AdminSession(new DemoAdminApiClient(), store, Server);

        await first.SignInAsync(" admin ", DemoAdminApiClient.DemoPassword, CancellationToken.None);

        Assert.True(first.IsSignedIn);
        Assert.Equal("admin", first.Username);
        Assert.Equal(Server, store.Saved?.ServerUrl);

        // A new client process: a fresh API client, the same file.
        var second = new AdminSession(new DemoAdminApiClient(), store, Server + "/");
        Assert.True(second.IsSignedIn);
        Assert.Equal("admin", second.Username);
        Assert.NotNull(await second.Api.GetDashboardAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_wrong_password_stores_nothing()
    {
        var store = new MemoryStore();
        var session = new AdminSession(new DemoAdminApiClient(), store, Server);

        var ex = await Assert.ThrowsAsync<PortalApiException>(() => session.SignInAsync("admin", "guess", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.False(session.IsSignedIn);
        Assert.Null(store.Saved);
    }

    [Fact]
    public void A_session_from_another_server_is_never_sent_to_this_one()
    {
        var store = new MemoryStore { Saved = new StoredAdminSession("https://old.example", "admin", "apa_old") };
        var api = new DemoAdminApiClient();

        var session = new AdminSession(api, store, Server);

        Assert.False(session.IsSignedIn);
        Assert.Null(api.Token);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task Signing_out_forgets_the_session_without_announcing_it()
    {
        var store = new MemoryStore();
        var session = new AdminSession(new DemoAdminApiClient(), store, Server);
        await session.SignInAsync("admin", DemoAdminApiClient.DemoPassword, CancellationToken.None);
        var ended = 0;
        session.Ended += (_, _) => ended++;

        await session.SignOutAsync(CancellationToken.None);

        Assert.False(session.IsSignedIn);
        Assert.Null(session.Username);
        Assert.Null(store.Saved);
        Assert.Equal(0, ended);
    }

    [Fact]
    public async Task A_refused_token_ends_the_session_and_says_why()
    {
        // What a revoke on the web leaves behind: a token on disk the server no longer honours.
        var store = new MemoryStore { Saved = new StoredAdminSession(Server, "admin", "apa_revoked") };
        var session = new AdminSession(new DemoAdminApiClient(), store, Server);
        Assert.True(session.IsSignedIn);
        string? notice = null;
        session.Ended += (_, message) => notice = message;

        await Assert.ThrowsAsync<PortalApiException>(() => session.Api.GetDashboardAsync(CancellationToken.None));

        Assert.Equal(AdminApiClient.SessionEndedMessage, notice);
        Assert.False(session.IsSignedIn);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task Signing_out_of_a_session_the_server_already_ended_is_still_quiet()
    {
        var store = new MemoryStore { Saved = new StoredAdminSession(Server, "admin", "apa_revoked") };
        var session = new AdminSession(new DemoAdminApiClient(), store, Server);
        var ended = 0;
        session.Ended += (_, _) => ended++;

        await session.SignOutAsync(CancellationToken.None);

        Assert.False(session.IsSignedIn);
        Assert.Null(store.Saved);
        Assert.Equal(0, ended);
    }

    [Fact]
    public void The_default_store_keeps_nothing_off_Windows()
    {
        var store = AdminSession.DefaultStore();
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<ProtectedAdminSessionStore>(store);
            return;
        }

        Assert.IsType<NoAdminSessionStore>(store);
        store.Save(new StoredAdminSession(Server, "admin", "apa_secret"));
        Assert.Null(store.Load());
    }

    [Fact]
    public void The_protected_store_round_trips_and_is_unreadable_on_disk()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("app-portal-admin-");
        try
        {
            var path = Path.Combine(directory.FullName, "AppPortal", "admin-session.bin");
            var store = new ProtectedAdminSessionStore(path);
            var session = new StoredAdminSession(Server, "admin", "apa_0123456789abcdef");

            store.Save(session);

            var onDisk = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("apa_0123456789abcdef", onDisk);
            Assert.DoesNotContain("portal.example", onDisk);
            Assert.Equal(session, new ProtectedAdminSessionStore(path).Load());

            store.Clear();
            Assert.False(File.Exists(path));
            Assert.Null(store.Load());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_protected_store_drops_a_file_it_cannot_decrypt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("app-portal-admin-");
        try
        {
            var path = Path.Combine(directory.FullName, "admin-session.bin");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("""{"serverUrl":"x","username":"admin","token":"apa_plain"}"""));

            Assert.Null(new ProtectedAdminSessionStore(path).Load());
            Assert.False(File.Exists(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_demo_account_is_admin_with_the_password_demo()
    {
        var api = new DemoAdminApiClient();

        var signedIn = await api.SignInAsync("admin", "demo", null, CancellationToken.None);
        Assert.StartsWith("apa_", signedIn.Token);

        await Assert.ThrowsAsync<PortalApiException>(() => api.SignInAsync("admin", "Demo", null, CancellationToken.None));

        // Without the token the demo refuses the way the server would, so the ended path shows up in demo mode too.
        var raised = 0;
        api.Unauthorized += (_, _) => raised++;
        await Assert.ThrowsAsync<PortalApiException>(() => api.GetDashboardAsync(CancellationToken.None));
        Assert.Equal(1, raised);

        api.Token = signedIn.Token;
        var counts = await api.GetDashboardAsync(CancellationToken.None);
        Assert.True(counts.Devices > 0);
        Assert.True(counts.PendingRequests > 0);
    }

    private sealed class MemoryStore : IAdminSessionStore
    {
        public StoredAdminSession? Saved { get; set; }

        public StoredAdminSession? Load() => Saved;

        public void Save(StoredAdminSession session) => Saved = session;

        public void Clear() => Saved = null;
    }
}
