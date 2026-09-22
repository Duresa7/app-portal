using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AppPortal.Client.Services;

/// <summary>
/// An administrator signed in on this PC, for this Windows account. The device token says which PC
/// this is; this says who is administering from it, and it survives a restart of the client.
/// </summary>
public interface IAdminSession
{
    bool IsSignedIn { get; }

    string? Username { get; }

    /// <summary>The admin API, carrying this session's token whenever there is one.</summary>
    IAdminApiClient Api { get; }

    /// <summary>Throws <see cref="PortalApiException"/> with the server's reason when it refuses.</summary>
    Task SignInAsync(string username, string password, CancellationToken ct);

    Task SignOutAsync(CancellationToken ct);

    /// <summary>
    /// The server stopped accepting the session on its own: revoked, expired, or the account disabled.
    /// The argument is the sentence to show. Not raised by <see cref="SignOutAsync"/>.
    /// </summary>
    event EventHandler<string>? Ended;
}

/// <summary>
/// What is kept between runs. The server address is kept with the token so a token issued by one
/// server is never sent to another after client.json is pointed somewhere new.
/// </summary>
public sealed record StoredAdminSession(string ServerUrl, string Username, string Token);

public interface IAdminSessionStore
{
    StoredAdminSession? Load();
    void Save(StoredAdminSession session);
    void Clear();
}

public sealed class AdminSession : IAdminSession
{
    private readonly IAdminSessionStore _store;
    private readonly string _serverUrl;
    private bool _signingOut;

    public AdminSession(IAdminApiClient api, IAdminSessionStore store, string serverUrl)
    {
        Api = api;
        _store = store;
        _serverUrl = Normalise(serverUrl);
        Api.Unauthorized += OnUnauthorized;

        // No expiry check here. The server slides the thirty days on every call, so the date it gave at
        // sign-in is only a lower bound, and the server is the one place that knows whether the session
        // still stands. The first call it refuses ends it, which is the same path as a revoke.
        var stored = Load();
        if (stored is not null && string.Equals(Normalise(stored.ServerUrl), _serverUrl, StringComparison.OrdinalIgnoreCase))
        {
            Api.Token = stored.Token;
            Username = stored.Username;
        }
        else if (stored is not null)
        {
            _store.Clear();
        }
    }

    public IAdminApiClient Api { get; }

    public bool IsSignedIn => Api.Token is not null;

    public string? Username { get; private set; }

    public event EventHandler<string>? Ended;

    /// <summary>
    /// DPAPI on Windows, so only this Windows account on this PC can read the file back. Anywhere else
    /// (the Linux CI, a developer box) nothing is written: a token in a readable file is worse than
    /// signing in again.
    /// </summary>
    public static IAdminSessionStore DefaultStore()
        => OperatingSystem.IsWindows()
            ? new ProtectedAdminSessionStore(ProtectedAdminSessionStore.DefaultPath)
            : NoAdminSessionStore.Instance;

    public async Task SignInAsync(string username, string password, CancellationToken ct)
    {
        var name = username.Trim();
        var signedIn = await Api.SignInAsync(name, password, Environment.MachineName, ct);
        Api.Token = signedIn.Token;
        Username = name;
        try
        {
            _store.Save(new StoredAdminSession(_serverUrl, name, signedIn.Token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Signed in all the same; this run works and the next one asks for the password again.
        }
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        if (Api.Token is null)
        {
            return;
        }

        _signingOut = true;
        try
        {
            // Revoked on the server as well, so the token is dead even if a copy of the file survives.
            await Api.SignOutAsync(ct);
        }
        catch (PortalApiException)
        {
            // Unreachable or already gone. Signing out on this PC must still work; the server lets the
            // session lapse on its own.
        }
        finally
        {
            _signingOut = false;
        }

        Forget();
    }

    private void OnUnauthorized(object? sender, EventArgs e)
    {
        // A token refused while signing out was on its way out anyway; that is not news to announce.
        if (Api.Token is null || _signingOut)
        {
            return;
        }

        Forget();
        Ended?.Invoke(this, AdminApiClient.SessionEndedMessage);
    }

    private void Forget()
    {
        Api.Token = null;
        Username = null;
        try
        {
            _store.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The token in it no longer works, so a file that would not delete is litter, not a leak.
        }
    }

    private StoredAdminSession? Load()
    {
        try
        {
            return _store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Normalise(string url) => url.Trim().TrimEnd('/');
}

/// <summary>
/// The session file, <c>%LocalAppData%\AppPortal\admin-session.bin</c>, encrypted with DPAPI for the
/// current Windows user. Another account on the same PC, an administrator's included, reads bytes it
/// cannot decrypt, and so does anyone who copies the file to another machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProtectedAdminSessionStore(string path) : IAdminSessionStore
{
    /// <summary>
    /// Not a secret. It stops another program running as the same user from decrypting the file with a
    /// bare <c>Unprotect</c> call unless it also knows it is App Portal's.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AppPortal.AdminSession.v1");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPortal", "admin-session.bin");

    public string FilePath { get; } = path;

    public StoredAdminSession? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            var session = JsonSerializer.Deserialize<StoredAdminSession>(plain, Json);
            return session is { Token.Length: > 0, Username.Length: > 0, ServerUrl.Length: > 0 } ? session : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Written by another account, a profile restored onto another PC, or a damaged file. None of
            // them can be recovered, and leaving it would fail the same way on every start.
            Clear();
            return null;
        }
    }

    public void Save(StoredAdminSession session)
    {
        var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(session, Json), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, cipher);
    }

    public void Clear()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}

/// <summary>Keeps nothing. Demo mode uses it so a sample session never replaces a real one on disk.</summary>
public sealed class NoAdminSessionStore : IAdminSessionStore
{
    public static readonly NoAdminSessionStore Instance = new();

    public StoredAdminSession? Load() => null;

    public void Save(StoredAdminSession session)
    {
    }

    public void Clear()
    {
    }
}
