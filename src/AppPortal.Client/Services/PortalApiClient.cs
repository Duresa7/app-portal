using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AppPortal.Shared;

namespace AppPortal.Client.Services;

public sealed class PortalApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public interface IPortalApiClient
{
    Task<IReadOnlyList<CatalogApp>> GetCatalogAsync(CancellationToken ct);
    Task<DeviceInfo> GetDeviceAsync(CancellationToken ct);
    Task<IReadOnlyList<InstalledApp>> GetInstalledAsync(CancellationToken ct);
    Task<IReadOnlyList<InstallRequest>> GetInstallsAsync(CancellationToken ct);
    Task<InstallRequest> RequestInstallAsync(string appId, CancellationToken ct);
}

public sealed class PortalApiClient : IPortalApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;

    public PortalApiClient(ClientSettings settings)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.DeviceToken);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AppPortal.Client/0.1");
    }

    public Task<IReadOnlyList<CatalogApp>> GetCatalogAsync(CancellationToken ct)
        => GetAsync<IReadOnlyList<CatalogApp>>(ApiRoutes.Catalog, ct);

    public Task<DeviceInfo> GetDeviceAsync(CancellationToken ct)
        => GetAsync<DeviceInfo>(ApiRoutes.Device, ct);

    public Task<IReadOnlyList<InstalledApp>> GetInstalledAsync(CancellationToken ct)
        => GetAsync<IReadOnlyList<InstalledApp>>(ApiRoutes.Installed, ct);

    public Task<IReadOnlyList<InstallRequest>> GetInstallsAsync(CancellationToken ct)
        => GetAsync<IReadOnlyList<InstallRequest>>(ApiRoutes.Installs, ct);

    public async Task<InstallRequest> RequestInstallAsync(string appId, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.PostAsJsonAsync(ApiRoutes.Installs.TrimStart('/'), new CreateInstallRequest(appId), Json, ct), ct);
        await ThrowIfFailedAsync(response, ct);
        return await ReadAsync<InstallRequest>(response, ct);
    }

    private async Task<T> GetAsync<T>(string route, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.GetAsync(route.TrimStart('/'), ct), ct);
        await ThrowIfFailedAsync(response, ct);
        return await ReadAsync<T>(response, ct);
    }

    /// <summary>
    /// An empty body throws JsonException rather than deserializing to null, and a proxy in front of
    /// the server can answer with a content type System.Text.Json refuses. Both have to come back as
    /// a PortalApiException, or they escape the caller and take the process with them.
    /// </summary>
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

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException ex)
        {
            throw new PortalApiException("Cannot reach the App Portal server. " + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PortalApiException("The App Portal server did not answer in time.");
        }
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message;
        try
        {
            message = (await response.Content.ReadFromJsonAsync<ErrorMessage>(Json, ct))?.Message ?? $"HTTP {(int)response.StatusCode}";
        }
        catch (JsonException)
        {
            message = $"HTTP {(int)response.StatusCode}";
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            message = "This device is not registered with the App Portal server.";
        }

        throw new PortalApiException(message, response.StatusCode);
    }
}
