using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Shared;

namespace AppPortal.Setup.Services;

/// <summary>What <c>GET /api/v1/enroll/check</c> answers with when the key is usable.</summary>
public sealed record EnrollmentCheck(string Engine);

/// <summary>
/// The real server, asked over HTTP. Every failure is turned into a sentence a tech can act on: at this
/// point in the job the useful answer is "the address is wrong" or "the key is spent", never a status code.
/// </summary>
public sealed class HttpEnrollmentProbe(HttpClient http) : IEnrollmentProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Short: a tech is watching the field, and a wrong address should not look like a hang.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public HttpEnrollmentProbe()
        : this(new HttpClient { Timeout = Timeout })
    {
    }

    public async Task<ProbeResult> ReachAsync(string serverUrl, CancellationToken ct)
    {
        if (SetupArguments.ServerUrlProblem(serverUrl) is { } problem)
        {
            return ProbeResult.Bad(problem);
        }

        try
        {
            using var response = await http.GetAsync(Address(serverUrl, "healthz"), ct);
            return response.IsSuccessStatusCode
                ? ProbeResult.Good()
                : ProbeResult.Bad($"The server answered {(int)response.StatusCode} at /healthz. Check the address.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && Expected(ex))
        {
            return ProbeResult.Bad($"Could not reach {serverUrl.Trim()}. Check the address, the network and any proxy.");
        }
    }

    public async Task<ProbeResult> CheckKeyAsync(string serverUrl, string enrollmentKey, CancellationToken ct)
    {
        if (SetupArguments.ServerUrlProblem(serverUrl) is { } problem)
        {
            return ProbeResult.Bad(problem);
        }

        if (string.IsNullOrWhiteSpace(enrollmentKey))
        {
            return ProbeResult.Bad("Enter an enrollment key from the server's Keys page.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Address(serverUrl, ApiRoutes.EnrollCheck.TrimStart('/')));
            request.Headers.Add(ApiHeaders.EnrollmentKey, enrollmentKey.Trim());
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return ProbeResult.Bad("That enrollment key is not usable. It may be revoked, expired or already used up.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ProbeResult.Bad("The server is refusing enrollment attempts from this address for a minute. Try again shortly.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProbeResult.Bad($"The server answered {(int)response.StatusCode} when the key was checked.");
            }

            // An older server answers 204 with no body. The key is good either way; without the engine
            // the wizard simply cannot tell whether an endpoint id is wanted, and asks for none.
            var answer = response.Content.Headers.ContentLength is > 0
                ? await response.Content.ReadFromJsonAsync<EnrollmentCheck>(Json, ct)
                : null;
            return ProbeResult.Good(answer?.Engine);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && Expected(ex))
        {
            return ProbeResult.Bad($"Could not reach {serverUrl.Trim()} to check the key.");
        }
    }

    public async Task<string?> DeviceNameAsync(string serverUrl, string deviceToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Address(serverUrl, ApiRoutes.Device.TrimStart('/')));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return (await response.Content.ReadFromJsonAsync<DeviceInfo>(Json, ct))?.DeviceName;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && Expected(ex))
        {
            return null;
        }
    }

    private static Uri Address(string serverUrl, string path)
        => new(new Uri(serverUrl.Trim().TrimEnd('/') + "/"), path);

    private static bool Expected(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or UriFormatException or JsonException or NotSupportedException;
}
