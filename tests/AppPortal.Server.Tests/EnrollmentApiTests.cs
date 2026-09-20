using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Devices;
using AppPortal.Server.Enrollment;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AppPortal.Server.Tests;

/// <summary>
/// The enrollment route, which is the only part of the device API a PC may call before it holds a
/// token. Every test here builds its own server, so the rate limiter each one meets is its own.
/// </summary>
public sealed class EnrollmentApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CapturedLogs _logs = new();

    public EnrollmentApiTests()
    {
        var catalogPath = Path.Combine(_test.Root, "catalog.json");
        File.WriteAllText(catalogPath, """
        {
          "apps": [
            { "id": "chrome", "name": "Google Chrome", "publisher": "Google", "description": "Browser", "category": "Browsers",
              "action1": { "packageId": "Google_Google_Chrome_1570243626751_builtin", "version": "latest" } }
          ]
        }
        """);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:CatalogPath", catalogPath);
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });
    }

    private EnrollmentKeyStore Keys => new(_test.Database);

    private DeviceStore Devices => new(_test.Database);

    private string ActiveKey(EnrollmentEngine engine = EnrollmentEngine.Agent, int? maxUses = null)
        => Keys.Create($"key-{Guid.NewGuid():N}", engine, null, maxUses, "tester").Plaintext;

    private async Task<HttpResponseMessage> Enroll(object body)
        => await _factory.CreateClient().PostAsJsonAsync(ApiRoutes.Enroll, body, Json);

    private static object Body(string key, string name = "PC-01", string machine = "machine-01", string? endpoint = null)
        => new { key, deviceName = name, machineId = machine, action1EndpointId = endpoint, agentVersion = "0.4.0" };

    [Fact]
    public async Task A_valid_key_yields_a_token_that_works_on_the_catalog()
    {
        var response = await Enroll(Body(ActiveKey()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var enrolled = await response.Content.ReadFromJsonAsync<EnrollResponse>(Json);
        Assert.NotNull(enrolled);
        Assert.Equal("PC-01", enrolled.DeviceName);
        Assert.Equal(["agent"], enrolled.Engines);
        Assert.StartsWith("apd_", enrolled.DeviceToken);

        var catalog = await Call(enrolled.DeviceToken, ApiRoutes.Catalog);
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
    }

    [Fact]
    public async Task Re_enrolling_the_same_machine_rotates_the_token_and_keeps_one_device()
    {
        var first = await (await Enroll(Body(ActiveKey()))).Content.ReadFromJsonAsync<EnrollResponse>(Json);
        var second = await (await Enroll(Body(ActiveKey(), name: "PC-01-RENAMED"))).Content.ReadFromJsonAsync<EnrollResponse>(Json);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.NotEqual(first.DeviceToken, second.DeviceToken);
        Assert.Single(Devices.All());
        Assert.Equal("PC-01-RENAMED", Devices.All()[0].Name);

        // The acceptance criterion: the token the machine held before stops working immediately.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(first.DeviceToken, ApiRoutes.Catalog)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(second.DeviceToken, ApiRoutes.Catalog)).StatusCode);
    }

    [Fact]
    public async Task A_reimaged_machine_is_recognised_by_name_rather_than_enrolled_twice()
    {
        var first = await (await Enroll(Body(ActiveKey()))).Content.ReadFromJsonAsync<EnrollResponse>(Json);
        var reimaged = await (await Enroll(Body(ActiveKey(), machine: "machine-after-reimage"))).Content.ReadFromJsonAsync<EnrollResponse>(Json);

        Assert.NotNull(first);
        Assert.NotNull(reimaged);
        Assert.Equal(first.DeviceId, reimaged.DeviceId);
        Assert.Single(Devices.All());
        Assert.Equal("machine-after-reimage", Devices.All()[0].MachineId);
    }

    [Fact]
    public async Task A_key_nobody_created_is_refused()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await Enroll(Body("ape_NOTAREALKEYATALLNOTAREALKEY1234"))).StatusCode);

    /// <summary>A blank key is a body that is missing a field, which is answered before any key is looked up.</summary>
    [Fact]
    public async Task A_blank_key_is_a_bad_request()
        => Assert.Equal(HttpStatusCode.BadRequest, (await Enroll(Body(""))).StatusCode);

    [Fact]
    public async Task A_revoked_key_is_refused_and_the_attempt_is_recorded_against_it()
    {
        var created = Keys.Create("revoked", EnrollmentEngine.Agent, null, null, "tester");
        Keys.Revoke(created.Key.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Enroll(Body(created.Plaintext))).StatusCode);

        var events = new EnrollmentEventStore(_test.Database).ForKey(created.Key.Id);
        Assert.Equal(EnrollmentOutcome.KeyRefused, Assert.Single(events).Outcome);
    }

    [Fact]
    public async Task An_exhausted_key_is_refused_on_the_second_machine()
    {
        var key = ActiveKey(maxUses: 1);
        Assert.Equal(HttpStatusCode.Created, (await Enroll(Body(key))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Enroll(Body(key, name: "PC-02", machine: "machine-02"))).StatusCode);
    }

    [Fact]
    public async Task A_body_missing_a_required_field_is_a_bad_request()
    {
        var response = await Enroll(new { key = ActiveKey(), deviceName = "PC-01" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_key_that_expects_action1_refuses_a_machine_that_names_no_endpoint()
    {
        var response = await Enroll(Body(ActiveKey(EnrollmentEngine.Action1)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var message = await response.Content.ReadFromJsonAsync<ErrorMessage>(Json);
        Assert.Contains("action1EndpointId", message!.Message);
    }

    [Fact]
    public async Task A_both_key_grants_both_engines()
    {
        var response = await Enroll(Body(ActiveKey(EnrollmentEngine.Both), endpoint: "endpoint-1234"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var enrolled = await response.Content.ReadFromJsonAsync<EnrollResponse>(Json);
        Assert.Equal(["action1", "agent"], enrolled!.Engines);
    }

    [Fact]
    public async Task An_agent_key_does_not_clear_an_endpoint_the_device_already_had()
    {
        Devices.Add("PC-01", "endpoint-1234");
        Assert.Equal(HttpStatusCode.Created, (await Enroll(Body(ActiveKey()))).StatusCode);

        var device = Devices.FindByName("PC-01");
        Assert.Equal("endpoint-1234", device!.EndpointId);
        Assert.True(device.HasAgent);
    }

    [Fact]
    public async Task A_device_an_administrator_disabled_cannot_enroll_its_way_back()
    {
        Devices.Add("PC-01", "");
        var device = Devices.FindByName("PC-01")!;
        device.Enabled = false;
        Devices.Update(device);

        var response = await Enroll(Body(ActiveKey()));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(Devices.FindByName("PC-01")!.Enabled);
    }

    [Fact]
    public async Task Only_one_of_two_machines_racing_for_the_last_use_wins()
    {
        var key = ActiveKey(maxUses: 1);
        var attempts = Enumerable.Range(0, 2)
            .Select(i => Enroll(Body(key, name: $"PC-{i}", machine: $"machine-{i}")))
            .ToArray();
        var codes = (await Task.WhenAll(attempts)).Select(r => r.StatusCode).ToList();

        Assert.Single(codes, HttpStatusCode.Created);
        Assert.Single(codes, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_check_route_answers_without_spending_a_use()
    {
        var created = Keys.Create("checked", EnrollmentEngine.Agent, null, maxUses: 1, "tester");

        Assert.Equal(HttpStatusCode.OK, (await Check(created.Plaintext)).StatusCode);
        Assert.Equal(0, Keys.Find(created.Key.Id)!.Uses);

        // Still good for the one enrollment it was made for.
        Assert.Equal(HttpStatusCode.Created, (await Enroll(Body(created.Plaintext))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Check(created.Plaintext)).StatusCode);
    }

    [Theory]
    [InlineData(EnrollmentEngine.Action1, "action1")]
    [InlineData(EnrollmentEngine.Agent, "agent")]
    [InlineData(EnrollmentEngine.Both, "both")]
    public async Task The_check_route_names_the_engine_the_key_enrolls_for(EnrollmentEngine engine, string expected)
    {
        // The setup wizard asks for an Action1 endpoint id only when this says the key needs one, so
        // the name is a contract and not a label: action1, agent or both, and nothing else.
        var created = Keys.Create("engine-" + expected, engine, null, maxUses: 1, "tester");

        var answer = await (await Check(created.Plaintext)).Content.ReadFromJsonAsync<EnrollmentCheck>(Json);

        Assert.Equal(expected, answer!.Engine);
    }

    [Fact]
    public async Task The_check_route_refuses_a_key_nobody_created()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Check("ape_NOTAREALKEYATALLNOTAREALKEY1234")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Check(null)).StatusCode);
    }

    [Fact]
    public async Task Attempts_past_the_minute_allowance_are_refused()
    {
        var client = _factory.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < EnrollmentEndpoints.PerMinutePerSource + 1; i++)
        {
            var response = await client.PostAsJsonAsync(ApiRoutes.Enroll, Body("ape_NOTAREALKEYATALLNOTAREALKEY1234"), Json);
            codes.Add(response.StatusCode);
        }

        Assert.Equal(EnrollmentEndpoints.PerMinutePerSource, codes.Count(c => c == HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
    }

    [Fact]
    public async Task Neither_the_key_nor_the_token_reaches_the_log()
    {
        var key = ActiveKey();
        var enrolled = await (await Enroll(Body(key))).Content.ReadFromJsonAsync<EnrollResponse>(Json);
        await Enroll(Body("ape_NOTAREALKEYATALLNOTAREALKEY1234", name: "PC-02", machine: "machine-02"));

        var written = _logs.Text();
        Assert.NotEqual("", written);
        Assert.DoesNotContain(key, written, StringComparison.Ordinal);
        Assert.DoesNotContain(enrolled!.DeviceToken, written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_key_page_shows_what_the_key_let_in()
    {
        _test.AddAdmin();
        var created = Keys.Create("rollout", EnrollmentEngine.Agent, null, null, "tester");
        Assert.Equal(HttpStatusCode.Created, (await Enroll(Body(created.Plaintext, name: "PC-LISTED"))).StatusCode);

        var page = await (await TestDatabase.SignedIn(_factory)).GetStringAsync($"/admin/keys/{created.Key.Id}");
        Assert.Contains("PC-LISTED", page);
        Assert.Contains("Enrolled", page);
    }

    private Task<HttpResponseMessage> Check(string? key)
    {
        var client = _factory.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add(ApiHeaders.EnrollmentKey, key);
        }

        return client.GetAsync(ApiRoutes.EnrollCheck);
    }

    private Task<HttpResponseMessage> Call(string token, string path)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.GetAsync(path);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }

    /// <summary>
    /// Keeps everything the server logged, so a test can assert that a secret never appeared in it.
    /// </summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public ILogger CreateLogger(string categoryName) => new Sink(_lines);

        public string Text() => string.Join("\n", _lines);

        public void Dispose()
        {
        }

        private sealed class Sink(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var line = new StringBuilder(formatter(state, exception));
                if (exception is not null)
                {
                    line.Append(' ').Append(exception);
                }

                lines.Enqueue(line.ToString());
            }
        }
    }
}
