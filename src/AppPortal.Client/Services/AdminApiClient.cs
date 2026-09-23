using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Shared;

namespace AppPortal.Client.Services;

/// <summary>What the server hands back for a user name and password: an <c>apa_</c> token and when it lapses.</summary>
public sealed record AdminSignedIn(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// The filters the fleet install history takes, named as the web page names them. Every field is
/// optional; a null one is left off the query string rather than sent empty.
/// </summary>
public sealed record AdminInstallFilter(
    string? Device = null,
    string? AppId = null,
    InstallState? State = null,
    string? Requester = null,
    DateOnly? From = null,
    DateOnly? To = null,
    bool AwaitingRestart = false);

/// <summary>
/// The admin JSON API of M4-01, one typed method per route. Same server as the device client, a
/// different credential: an administrator's <c>apa_</c> session token instead of this PC's device token.
/// </summary>
public interface IAdminApiClient
{
    /// <summary>The session token every call carries. Null until somebody signs in.</summary>
    string? Token { get; set; }

    /// <summary>
    /// Raised when the server refuses <see cref="Token"/> on any call but the sign-in itself: the session
    /// was revoked, it expired, or the account behind it was disabled. The call still throws.
    /// </summary>
    event EventHandler? Unauthorized;

    Task<AdminSignedIn> SignInAsync(string username, string password, string? deviceName, CancellationToken ct);
    Task SignOutAsync(CancellationToken ct);
    Task<IReadOnlyList<AdminSessionSummary>> GetSessionsAsync(CancellationToken ct);
    Task RevokeSessionAsync(string id, CancellationToken ct);

    Task<DashboardCounts> GetDashboardAsync(CancellationToken ct);

    Task<AdminPage<AdminInstall>> GetInstallsAsync(AdminInstallFilter filter, int offset, int limit, CancellationToken ct);
    Task<AdminInstall> GetInstallAsync(string id, CancellationToken ct);
    Task<AdminInstall> CancelInstallAsync(string id, CancellationToken ct);

    /// <summary>Requests with one status, or every request when <paramref name="status"/> is null.</summary>
    Task<AdminPage<AdminRequest>> GetRequestsAsync(AppRequestStatus? status, int offset, int limit, CancellationToken ct);
    /// <summary>Approves a request, naming the catalog app that answers it when <paramref name="catalogAppId"/> is set.</summary>
    Task<AdminRequest> ApproveRequestAsync(string id, string? reason, string? catalogAppId, CancellationToken ct);
    Task<AdminRequest> DenyRequestAsync(string id, string? reason, CancellationToken ct);

    /// <summary>Names, changes or (null) removes the catalog app an approved request is answered by.</summary>
    Task<AdminRequest> LinkRequestAsync(string id, string? catalogAppId, CancellationToken ct);

    Task<AdminPage<AdminCatalogApp>> GetCatalogAsync(string? search, int offset, int limit, CancellationToken ct);
    Task<AdminCatalogApp> GetCatalogAppAsync(string id, CancellationToken ct);

    /// <summary>Creates the app or replaces it whole. The id in <paramref name="app"/> names which.</summary>
    Task<AdminCatalogApp> SaveCatalogAppAsync(AdminCatalogApp app, CancellationToken ct);
    Task DeleteCatalogAppAsync(string id, CancellationToken ct);
    Task<AdminCatalogApp> SetCatalogAppHiddenAsync(string id, bool hidden, CancellationToken ct);
    Task<AdminCatalogImported> ImportCatalogAsync(string catalogJson, CancellationToken ct);
    Task<string> ExportCatalogAsync(CancellationToken ct);
    Task<IReadOnlyList<AdminPackageResult>> SearchAction1PackagesAsync(string term, CancellationToken ct);
    Task<AdminPackageVerified> VerifyAction1PackageAsync(AdminPackageRef package, CancellationToken ct);
    Task<AdminInstallerHash> HashInstallerAsync(string url, CancellationToken ct);
    Task<AdminWingetLookup> LookupWingetAsync(AdminPackageRef package, CancellationToken ct);

    Task<AdminPage<AdminDevice>> GetDevicesAsync(string? search, int offset, int limit, CancellationToken ct);
    Task<AdminDeviceDetail> GetDeviceAsync(string id, CancellationToken ct);
    Task<AdminDeviceToken> CreateDeviceAsync(AdminDeviceCreate device, CancellationToken ct);
    Task<AdminDevice> UpdateDeviceAsync(string id, AdminDeviceUpdate update, CancellationToken ct);
    Task<AdminDeviceToken> RotateDeviceTokenAsync(string id, CancellationToken ct);
    Task DeleteDeviceAsync(string id, CancellationToken ct);

    Task<AdminPage<EnrollmentKeySummary>> GetKeysAsync(int offset, int limit, CancellationToken ct);
    Task<EnrollmentKeySummary> GetKeyAsync(string id, CancellationToken ct);
    Task<EnrollmentKeyCreated> CreateKeyAsync(EnrollmentKeyCreate key, CancellationToken ct);
    Task<EnrollmentKeySummary> RevokeKeyAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<EnrollmentKeyEvent>> GetKeyEventsAsync(string id, int? limit, CancellationToken ct);

    Task<AdminPage<AdminAccount>> GetAdminsAsync(int offset, int limit, CancellationToken ct);
    Task<AdminAccount> CreateAdminAsync(AdminAccountCreate account, CancellationToken ct);
    Task<AdminAccount> DisableAdminAsync(string id, CancellationToken ct);
    Task ResetAdminPasswordAsync(string id, string password, CancellationToken ct);

    Task<AdminSettings> GetSettingsAsync(CancellationToken ct);
    Task<AdminSettings> UpdateSettingsAsync(AdminSettings settings, CancellationToken ct);
}

public sealed class AdminApiClient : IAdminApiClient
{
    /// <summary>What a person reads when the server stops accepting their session.</summary>
    public const string SessionEndedMessage = "Your administrator session has ended. Sign in again to continue.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;

    /// <param name="serverUrl">The same address the device client uses.</param>
    /// <param name="handler">Replaced in tests; null is the ordinary network stack.</param>
    public AdminApiClient(string serverUrl, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AppPortal.Client/0.1");
    }

    public string? Token { get; set; }

    public event EventHandler? Unauthorized;

    public Task<AdminSignedIn> SignInAsync(string username, string password, string? deviceName, CancellationToken ct)
        => SendAsync<AdminSignedIn>(HttpMethod.Post, AdminApiRoutes.Session, new SignInBody(username, password, deviceName), ct, signingIn: true);

    public Task SignOutAsync(CancellationToken ct)
        => SendAsync(HttpMethod.Delete, AdminApiRoutes.Session, null, ct);

    public Task<IReadOnlyList<AdminSessionSummary>> GetSessionsAsync(CancellationToken ct)
        => SendAsync<IReadOnlyList<AdminSessionSummary>>(HttpMethod.Get, AdminApiRoutes.Sessions, null, ct);

    public Task RevokeSessionAsync(string id, CancellationToken ct)
        => SendAsync(HttpMethod.Delete, Item(AdminApiRoutes.Sessions, id), null, ct);

    public Task<DashboardCounts> GetDashboardAsync(CancellationToken ct)
        => SendAsync<DashboardCounts>(HttpMethod.Get, AdminApiRoutes.Dashboard, null, ct);

    public Task<AdminPage<AdminInstall>> GetInstallsAsync(AdminInstallFilter filter, int offset, int limit, CancellationToken ct)
    {
        // The server reads these with the page's own field names, so the client sends exactly those.
        var query = new List<KeyValuePair<string, string?>>
        {
            new("Device", filter.Device),
            new("App", filter.AppId),
            new("State", filter.State?.ToString()),
            new("Requester", filter.Requester),
            new("From", filter.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new("To", filter.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new("Restart", filter.AwaitingRestart ? "1" : null),
        };
        return SendAsync<AdminPage<AdminInstall>>(HttpMethod.Get, Paged(AdminApiRoutes.Installs, offset, limit, query), null, ct);
    }

    public Task<AdminInstall> GetInstallAsync(string id, CancellationToken ct)
        => SendAsync<AdminInstall>(HttpMethod.Get, Item(AdminApiRoutes.Installs, id), null, ct);

    public Task<AdminInstall> CancelInstallAsync(string id, CancellationToken ct)
        => SendAsync<AdminInstall>(HttpMethod.Post, Item(AdminApiRoutes.Installs, id, "cancel"), null, ct);

    public Task<AdminPage<AdminRequest>> GetRequestsAsync(AppRequestStatus? status, int offset, int limit, CancellationToken ct)
    {
        // "all" is said out loud rather than left off, so the answer does not depend on a server default.
        var name = status?.ToString().ToLowerInvariant() ?? "all";
        return SendAsync<AdminPage<AdminRequest>>(HttpMethod.Get, Paged(AdminApiRoutes.Requests, offset, limit, [new("status", name)]), null, ct);
    }

    public Task<AdminRequest> ApproveRequestAsync(string id, string? reason, string? catalogAppId, CancellationToken ct)
        => SendAsync<AdminRequest>(HttpMethod.Post, Item(AdminApiRoutes.Requests, id, "approve"), new AdminDecision(reason, catalogAppId), ct);

    public Task<AdminRequest> DenyRequestAsync(string id, string? reason, CancellationToken ct)
        => SendAsync<AdminRequest>(HttpMethod.Post, Item(AdminApiRoutes.Requests, id, "deny"), new AdminDecision(reason), ct);

    public Task<AdminRequest> LinkRequestAsync(string id, string? catalogAppId, CancellationToken ct)
        => SendAsync<AdminRequest>(HttpMethod.Put, Item(AdminApiRoutes.Requests, id, "catalog-app"), new AdminRequestLink(catalogAppId), ct);

    public Task<AdminPage<AdminCatalogApp>> GetCatalogAsync(string? search, int offset, int limit, CancellationToken ct)
        => SendAsync<AdminPage<AdminCatalogApp>>(HttpMethod.Get, Paged(AdminApiRoutes.Catalog, offset, limit, [new("Search", search)]), null, ct);

    public Task<AdminCatalogApp> GetCatalogAppAsync(string id, CancellationToken ct)
        => SendAsync<AdminCatalogApp>(HttpMethod.Get, Item(AdminApiRoutes.Catalog, id), null, ct);

    public Task<AdminCatalogApp> SaveCatalogAppAsync(AdminCatalogApp app, CancellationToken ct)
        => SendAsync<AdminCatalogApp>(HttpMethod.Put, Item(AdminApiRoutes.Catalog, app.Id), app, ct);

    public Task DeleteCatalogAppAsync(string id, CancellationToken ct)
        => SendAsync(HttpMethod.Delete, Item(AdminApiRoutes.Catalog, id), null, ct);

    public Task<AdminCatalogApp> SetCatalogAppHiddenAsync(string id, bool hidden, CancellationToken ct)
        => SendAsync<AdminCatalogApp>(HttpMethod.Post, Item(AdminApiRoutes.Catalog, id, "hidden"), new AdminCatalogHidden(hidden), ct);

    public Task<AdminCatalogImported> ImportCatalogAsync(string catalogJson, CancellationToken ct)
    {
        // The catalog file is the body as it is, not wrapped: the server reads it exactly as the web
        // form's upload, and re-serialising it here would be a chance to change what the file says.
        var content = new StringContent(catalogJson, Encoding.UTF8, "application/json");
        return SendAsync<AdminCatalogImported>(HttpMethod.Post, AdminApiRoutes.Catalog + "/import", content, ct);
    }

    public async Task<string> ExportCatalogAsync(CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, AdminApiRoutes.Catalog + "/export", null, ct, signingIn: false);
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PortalApiException("The connection dropped while reading the response. " + ex.Message);
        }
    }

    public Task<IReadOnlyList<AdminPackageResult>> SearchAction1PackagesAsync(string term, CancellationToken ct)
        => SendAsync<IReadOnlyList<AdminPackageResult>>(HttpMethod.Post, AdminApiRoutes.Catalog + "/action1/search", new AdminPackageSearch(term), ct);

    public Task<AdminPackageVerified> VerifyAction1PackageAsync(AdminPackageRef package, CancellationToken ct)
        => SendAsync<AdminPackageVerified>(HttpMethod.Post, AdminApiRoutes.Catalog + "/action1/verify", package, ct);

    public Task<AdminInstallerHash> HashInstallerAsync(string url, CancellationToken ct)
        => SendAsync<AdminInstallerHash>(HttpMethod.Post, AdminApiRoutes.Catalog + "/package/hash", new AdminInstallerRequest(url), ct);

    public Task<AdminWingetLookup> LookupWingetAsync(AdminPackageRef package, CancellationToken ct)
        => SendAsync<AdminWingetLookup>(HttpMethod.Post, AdminApiRoutes.Catalog + "/package/winget", package, ct);

    public Task<AdminPage<AdminDevice>> GetDevicesAsync(string? search, int offset, int limit, CancellationToken ct)
        => SendAsync<AdminPage<AdminDevice>>(HttpMethod.Get, Paged(AdminApiRoutes.Devices, offset, limit, [new("Search", search)]), null, ct);

    public Task<AdminDeviceDetail> GetDeviceAsync(string id, CancellationToken ct)
        => SendAsync<AdminDeviceDetail>(HttpMethod.Get, Item(AdminApiRoutes.Devices, id), null, ct);

    public Task<AdminDeviceToken> CreateDeviceAsync(AdminDeviceCreate device, CancellationToken ct)
        => SendAsync<AdminDeviceToken>(HttpMethod.Post, AdminApiRoutes.Devices, device, ct);

    public Task<AdminDevice> UpdateDeviceAsync(string id, AdminDeviceUpdate update, CancellationToken ct)
        => SendAsync<AdminDevice>(HttpMethod.Put, Item(AdminApiRoutes.Devices, id), update, ct);

    public Task<AdminDeviceToken> RotateDeviceTokenAsync(string id, CancellationToken ct)
        => SendAsync<AdminDeviceToken>(HttpMethod.Post, Item(AdminApiRoutes.Devices, id, "rotate-token"), null, ct);

    public Task DeleteDeviceAsync(string id, CancellationToken ct)
        => SendAsync(HttpMethod.Delete, Item(AdminApiRoutes.Devices, id), null, ct);

    public Task<AdminPage<EnrollmentKeySummary>> GetKeysAsync(int offset, int limit, CancellationToken ct)
        => SendAsync<AdminPage<EnrollmentKeySummary>>(HttpMethod.Get, Paged(AdminApiRoutes.Keys, offset, limit, []), null, ct);

    public Task<EnrollmentKeySummary> GetKeyAsync(string id, CancellationToken ct)
        => SendAsync<EnrollmentKeySummary>(HttpMethod.Get, Item(AdminApiRoutes.Keys, id), null, ct);

    public Task<EnrollmentKeyCreated> CreateKeyAsync(EnrollmentKeyCreate key, CancellationToken ct)
        => SendAsync<EnrollmentKeyCreated>(HttpMethod.Post, AdminApiRoutes.Keys, key, ct);

    public Task<EnrollmentKeySummary> RevokeKeyAsync(string id, CancellationToken ct)
        => SendAsync<EnrollmentKeySummary>(HttpMethod.Post, Item(AdminApiRoutes.Keys, id, "revoke"), null, ct);

    public Task<IReadOnlyList<EnrollmentKeyEvent>> GetKeyEventsAsync(string id, int? limit, CancellationToken ct)
    {
        var route = Item(AdminApiRoutes.Keys, id, "events");
        if (limit is { } n)
        {
            route += "?limit=" + n.ToString(CultureInfo.InvariantCulture);
        }

        return SendAsync<IReadOnlyList<EnrollmentKeyEvent>>(HttpMethod.Get, route, null, ct);
    }

    public Task<AdminPage<AdminAccount>> GetAdminsAsync(int offset, int limit, CancellationToken ct)
        => SendAsync<AdminPage<AdminAccount>>(HttpMethod.Get, Paged(AdminApiRoutes.Admins, offset, limit, []), null, ct);

    public Task<AdminAccount> CreateAdminAsync(AdminAccountCreate account, CancellationToken ct)
        => SendAsync<AdminAccount>(HttpMethod.Post, AdminApiRoutes.Admins, account, ct);

    public Task<AdminAccount> DisableAdminAsync(string id, CancellationToken ct)
        => SendAsync<AdminAccount>(HttpMethod.Post, Item(AdminApiRoutes.Admins, id, "disable"), null, ct);

    public Task ResetAdminPasswordAsync(string id, string password, CancellationToken ct)
        => SendAsync(HttpMethod.Post, Item(AdminApiRoutes.Admins, id, "reset-password"), new AdminPasswordReset(password), ct);

    public Task<AdminSettings> GetSettingsAsync(CancellationToken ct)
        => SendAsync<AdminSettings>(HttpMethod.Get, AdminApiRoutes.Settings, null, ct);

    public Task<AdminSettings> UpdateSettingsAsync(AdminSettings settings, CancellationToken ct)
        => SendAsync<AdminSettings>(HttpMethod.Put, AdminApiRoutes.Settings, settings, ct);

    /// <summary>
    /// The server's own sign-in body. It lives in the server project, not the shared one, so the client
    /// writes the same three fields itself.
    /// </summary>
    private sealed record SignInBody(string Username, string Password, string? DeviceName);

    /// <summary>
    /// A route with an id, escaped. Ids come from the server, but a catalog id is whatever an
    /// administrator typed, and a slash or a question mark in one must not become a different route.
    /// </summary>
    private static string Item(string collection, string id, string? action = null)
        => collection + "/" + Uri.EscapeDataString(id) + (action is null ? "" : "/" + action);

    private static string Paged(string route, int offset, int limit, IEnumerable<KeyValuePair<string, string?>> filters)
    {
        var parts = new List<string>();
        foreach (var (key, value) in filters)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(key + "=" + Uri.EscapeDataString(value.Trim()));
            }
        }

        parts.Add("limit=" + limit.ToString(CultureInfo.InvariantCulture));
        parts.Add("offset=" + offset.ToString(CultureInfo.InvariantCulture));
        return route + "?" + string.Join("&", parts);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken ct, bool signingIn = false)
    {
        using var response = await SendRawAsync(method, route, body, ct, signingIn);
        return await ReadAsync<T>(response, ct);
    }

    private async Task SendAsync(HttpMethod method, string route, object? body, CancellationToken ct)
        => (await SendRawAsync(method, route, body, ct, signingIn: false)).Dispose();

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string route, object? body, CancellationToken ct, bool signingIn)
    {
        using var request = new HttpRequestMessage(method, route.TrimStart('/'));
        // Per request rather than on the client, because the token changes under a running client: it
        // arrives with a sign-in and goes with a sign-out, and the sign-in itself must not carry an old one.
        if (!signingIn && Token is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = body switch
        {
            null => null,
            HttpContent content => content,
            _ => JsonContent.Create(body, body.GetType(), options: Json),
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PortalApiException("Cannot reach the App Portal server. " + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PortalApiException("The App Portal server did not answer in time.");
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                await ThrowAsync(response, signingIn, ct);
            }
        }

        return response;
    }

    /// <summary>Same reasoning as the device client: a body this app cannot read must not escape as a crash.</summary>
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                   ?? throw new PortalApiException("The server returned an empty response.");
        }
        catch (JsonException)
        {
            throw new PortalApiException("The server returned a response this app could not read.");
        }
        catch (NotSupportedException)
        {
            throw new PortalApiException("The server returned an unexpected content type. Check the server address.");
        }
        catch (HttpRequestException ex)
        {
            throw new PortalApiException("The connection dropped while reading the response. " + ex.Message);
        }
    }

    private async Task ThrowAsync(HttpResponseMessage response, bool signingIn, CancellationToken ct)
    {
        var message = await ServerMessageAsync(response, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && !signingIn)
        {
            // A refused sign-in is a wrong password and stays on the form. A refused token anywhere else
            // means the session is over, whatever the page was doing, so whoever holds it is told first.
            Unauthorized?.Invoke(this, EventArgs.Empty);
            throw new PortalApiException(SessionEndedMessage, response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests && signingIn)
        {
            // The throttle answers with no body at all, so there is nothing of the server's to show.
            message ??= "Too many failed sign-ins for that user name. Wait fifteen minutes and try again.";
        }

        throw new PortalApiException(message ?? $"The server answered HTTP {(int)response.StatusCode}.", response.StatusCode);
    }

    /// <summary>
    /// The <see cref="ErrorMessage"/> every admin route writes, or null when the body is empty or not
    /// that shape, which is what a proxy's error page or the sign-in throttle sends.
    /// </summary>
    private static async Task<string?> ServerMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorMessage>(Json, ct);
            return string.IsNullOrWhiteSpace(error?.Message) ? null : error.Message;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            return null;
        }
    }
}
