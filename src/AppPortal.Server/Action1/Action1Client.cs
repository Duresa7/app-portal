using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Options;

using Microsoft.Extensions.Options;

namespace AppPortal.Server.Action1;

/// <summary>
/// Thin client for the Action1 RESTful API 3.0. Only the calls the portal needs are implemented:
/// OAuth token, endpoint lookup, installed-software inventory, package version lookup,
/// deploy-package automation, and automation status.
/// </summary>
public sealed class Action1Client : IAction1Client
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly Action1Options _options;
    private readonly ILogger<Action1Client> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public Action1Client(HttpClient http, IOptions<Action1Options> options, ILogger<Action1Client> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<Action1Endpoint?> GetEndpointAsync(string endpointId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"endpoints/managed/{_options.OrgId}/{Uri.EscapeDataString(endpointId)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct);
        var dto = await ReadJsonAsync<EndpointDto>(response, ct)
                  ?? throw new Action1Exception("Action1 returned an empty endpoint record.");
        return new Action1Endpoint(dto.Id ?? endpointId, dto.Name ?? dto.DeviceName ?? endpointId, dto.Status ?? "Unknown", Action1Time.Parse(dto.LastSeen));
    }

    public async Task<IReadOnlyList<Action1InstalledSoftware>> GetInstalledSoftwareAsync(string endpointId, CancellationToken ct)
    {
        var results = new List<Action1InstalledSoftware>();
        string? path = $"installed-software/{_options.OrgId}/data/{Uri.EscapeDataString(endpointId)}?limit=500";
        var pages = 0;
        while (path is not null && pages < 20)
        {
            pages++;
            using var response = await SendAsync(HttpMethod.Get, path, null, ct);
            await EnsureSuccessAsync(response, ct);
            var page = await ReadJsonAsync<ResultPage<ReportRow>>(response, ct);
            if (page?.Items is null)
            {
                break;
            }

            foreach (var row in page.Items)
            {
                var name = row.Field("Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                results.Add(new Action1InstalledSoftware(name, row.Field("Vendor") ?? "", row.Field("Version") ?? ""));
            }

            path = NextPagePath(page.NextPage);
        }

        return results;
    }

    public async Task<Action1PackageVersion?> ResolvePackageVersionAsync(string packageId, string requestedVersion, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"software-repository/{_options.OrgId}/{Uri.EscapeDataString(packageId)}?fields=versions", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct);
        var package = await ReadJsonAsync<PackageDto>(response, ct);
        var versions = package?.Versions?.Items ?? [];
        var published = versions
            .Where(v => !string.IsNullOrWhiteSpace(v.Version))
            .Where(v => v.Status is null || string.Equals(v.Status, "Published", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (published.Count == 0)
        {
            return null;
        }

        if (!string.Equals(requestedVersion, "latest", StringComparison.OrdinalIgnoreCase))
        {
            var exact = published.FirstOrDefault(v => string.Equals(v.Version, requestedVersion, StringComparison.OrdinalIgnoreCase));
            return exact is null ? null : new Action1PackageVersion(exact.Id ?? exact.Version!, exact.Version!);
        }

        var newest = published
            .OrderByDescending(v => Version.TryParse(v.Version, out var parsed) ? parsed : new Version(0, 0))
            .ThenByDescending(v => v.ReleaseDate)
            .First();
        return new Action1PackageVersion(newest.Id ?? newest.Version!, newest.Version!);
    }

    public async Task<string> StartDeploymentAsync(string endpointId, string automationName, string packageId, string version, string displaySummary, CancellationToken ct)
    {
        var body = new
        {
            name = automationName,
            retry_minutes = _options.RetryMinutes.ToString(),
            endpoints = new[] { new { id = endpointId, type = "Endpoint" } },
            actions = new object[]
            {
                new
                {
                    name = "Deploy Software",
                    template_id = "deploy_package",
                    @params = new
                    {
                        display_summary = displaySummary,
                        packages = new[] { new Dictionary<string, string> { [packageId] = version } },
                        reboot_options = new { auto_reboot = "no" },
                    },
                },
            },
        };

        using var response = await SendAsync(HttpMethod.Post, $"automations/instances/{_options.OrgId}", body, ct);
        await EnsureSuccessAsync(response, ct);
        var instance = await ReadJsonAsync<AutomationInstanceDto>(response, ct);
        if (string.IsNullOrWhiteSpace(instance?.Id))
        {
            throw new Action1Exception("Action1 accepted the deployment but returned no automation ID.");
        }

        return instance.Id;
    }

    public async Task<Action1DeploymentStatus> GetDeploymentStatusAsync(string automationId, string endpointId, CancellationToken ct)
    {
        using var results = await SendAsync(HttpMethod.Get, $"automations/instances/{_options.OrgId}/{Uri.EscapeDataString(automationId)}/endpoint-results?limit=100", null, ct);
        if (results.IsSuccessStatusCode)
        {
            var page = await ReadJsonAsync<ResultPage<EndpointResultDto>>(results, ct);
            var mine = page?.Items?.FirstOrDefault(r => string.Equals(r.Id, endpointId, StringComparison.OrdinalIgnoreCase))
                       ?? page?.Items?.FirstOrDefault();
            if (mine?.Status is not null)
            {
                return new Action1DeploymentStatus(mine.Status, ParsePercent(mine.PercentCompleted), mine.Description);
            }
        }

        using var instance = await SendAsync(HttpMethod.Get, $"automations/instances/{_options.OrgId}/{Uri.EscapeDataString(automationId)}", null, ct);
        await EnsureSuccessAsync(instance, ct);
        var dto = await ReadJsonAsync<AutomationInstanceDto>(instance, ct);
        return new Action1DeploymentStatus(dto?.Status ?? "Pending", ParsePercent(dto?.PercentCompleted), null);
    }

    public async Task<IReadOnlyList<Action1Package>> SearchPackagesAsync(string nameFilter, CancellationToken ct)
    {
        var results = new List<Action1Package>();
        string? path = $"software-repository/{_options.OrgId}?limit=200&filter={Uri.EscapeDataString(nameFilter)}";
        var pages = 0;
        while (path is not null && pages < 10)
        {
            pages++;
            using var response = await SendAsync(HttpMethod.Get, path, null, ct);
            await EnsureSuccessAsync(response, ct);
            var page = await ReadJsonAsync<ResultPage<PackageDto>>(response, ct);
            if (page?.Items is null)
            {
                break;
            }

            results.AddRange(page.Items
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .Select(p => new Action1Package(p.Id!, p.Name ?? "", p.Vendor ?? "", p.Id!.EndsWith("_builtin", StringComparison.OrdinalIgnoreCase))));
            path = NextPagePath(page.NextPage);
        }

        return results;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetTokenAsync(force: attempt > 0, ct);
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }

            var response = await _http.SendAsync(request, ct);
            // The error text names the request path; not every handler fills this in.
            response.RequestMessage ??= request;
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                continue;
            }

            return response;
        }

        throw new Action1Exception("Action1 rejected the API credentials twice in a row.");
    }

    private async Task<string> GetTokenAsync(bool force, CancellationToken ct)
    {
        if (!force && _accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!force && _accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            {
                return _accessToken;
            }

            if (string.IsNullOrWhiteSpace(_options.ClientId) || string.IsNullOrWhiteSpace(_options.ClientSecret))
            {
                throw new Action1Exception("Action1 API credentials are not configured. Set Action1__ClientId and Action1__ClientSecret.");
            }

            using var response = await _http.PostAsJsonAsync("oauth2/token",
                new { client_id = _options.ClientId, client_secret = _options.ClientSecret }, Json, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Action1 token request failed with {Status}", (int)response.StatusCode);
                throw new Action1Exception($"Action1 token request failed with HTTP {(int)response.StatusCode}.");
            }

            var token = await ReadJsonAsync<TokenDto>(response, ct);
            if (string.IsNullOrWhiteSpace(token?.AccessToken))
            {
                throw new Action1Exception("Action1 token response had no access token.");
            }

            _accessToken = token.AccessToken;
            var lifetime = token.ExpiresIn > 60 ? token.ExpiresIn - 60 : 300;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        _logger.LogWarning("Action1 call {Method} {Path} failed with {Status}: {Body}",
            response.RequestMessage?.Method, response.RequestMessage?.RequestUri?.PathAndQuery, (int)response.StatusCode, Truncate(text));
        throw new Action1Exception($"Action1 returned HTTP {(int)response.StatusCode} for {Describe(response)}.");
    }

    /// <summary>
    /// Action1 answers some lookups for an identifier it does not know with HTTP 200 and an empty body
    /// rather than 404. ReadFromJsonAsync throws on that, and the exception surfaced as a 500 from the
    /// API and a crash of the CLI. Read the body first, treat nothing as null, and turn a body that is
    /// not JSON into the Action1Exception every caller already handles.
    /// </summary>
    private async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : class
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (JsonException ex)
        {
            throw new Action1Exception($"Action1 returned a response that could not be read for {Describe(response)}.", ex);
        }
    }

    /// <summary>The request path with the organisation identifier taken out, since the text reaches device clients.</summary>
    private string Describe(HttpResponseMessage response)
    {
        var path = response.RequestMessage?.RequestUri?.AbsolutePath ?? "(unknown path)";
        return string.IsNullOrEmpty(_options.OrgId) ? path : path.Replace(_options.OrgId, "{org}", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NextPagePath(string? nextPage)
    {
        if (string.IsNullOrWhiteSpace(nextPage))
        {
            return null;
        }

        // Action1 returns either "/API/{path}" or an absolute URL. Keep the part after "/api/3.0/" or "/API/".
        var index = nextPage.IndexOf("/api/3.0/", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return nextPage[(index + "/api/3.0/".Length)..];
        }

        index = nextPage.IndexOf("/API/", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? nextPage[(index + "/API/".Length)..] : nextPage.TrimStart('/');
    }

    private static int ParsePercent(string? value)
        => int.TryParse(value, out var percent) ? Math.Clamp(percent, 0, 100) : 0;

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "...";

    private sealed class TokenDto
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class ResultPage<T>
    {
        public List<T>? Items { get; set; }
        [JsonPropertyName("next_page")] public string? NextPage { get; set; }
    }

    private sealed class EndpointDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        [JsonPropertyName("device_name")] public string? DeviceName { get; set; }
        public string? Status { get; set; }
        [JsonPropertyName("last_seen")] public string? LastSeen { get; set; }
    }

    private sealed class ReportRow
    {
        public Dictionary<string, JsonElement>? Fields { get; set; }

        public string? Field(string name)
        {
            if (Fields is null || !Fields.TryGetValue(name, out var element))
            {
                return null;
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                _ => null,
            };
        }
    }

    private sealed class PackageDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Vendor { get; set; }
        public VersionsContainer? Versions { get; set; }
    }

    /// <summary>Action1 returns "versions" either as a bare array or as a ResultPage envelope.</summary>
    [JsonConverter(typeof(VersionsContainerConverter))]
    private sealed class VersionsContainer
    {
        public List<PackageVersionDto> Items { get; set; } = [];
    }

    private sealed class VersionsContainerConverter : JsonConverter<VersionsContainer>
    {
        public override VersionsContainer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var container = new VersionsContainer();
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                container.Items = JsonSerializer.Deserialize<List<PackageVersionDto>>(ref reader, options) ?? [];
                return container;
            }

            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("items", out var items))
            {
                container.Items = items.Deserialize<List<PackageVersionDto>>(options) ?? [];
            }

            return container;
        }

        public override void Write(Utf8JsonWriter writer, VersionsContainer value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value.Items, options);
    }

    private sealed class PackageVersionDto
    {
        public string? Id { get; set; }
        public string? Version { get; set; }
        public string? Status { get; set; }
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    }

    private sealed class AutomationInstanceDto
    {
        public string? Id { get; set; }
        public string? Status { get; set; }
        [JsonPropertyName("percent_completed")] public string? PercentCompleted { get; set; }
    }

    private sealed class EndpointResultDto
    {
        public string? Id { get; set; }
        [JsonPropertyName("endpoint_name")] public string? EndpointName { get; set; }
        public string? Status { get; set; }
        public string? Description { get; set; }
        [JsonPropertyName("percent_completed")] public string? PercentCompleted { get; set; }
    }
}
