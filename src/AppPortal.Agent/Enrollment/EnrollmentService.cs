using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Win32;

namespace AppPortal.Agent.Enrollment;

public sealed record EnrollmentSettings(string ServerUrl, string EnrollmentKey, string? Action1EndpointId);

public sealed record EnrollmentRequest(string Key, string DeviceName, string MachineId, string? Action1EndpointId, string AgentVersion);

public sealed record EnrollmentResponse(string DeviceId, string DeviceToken, string DeviceName, string[] Engines);

/// <summary>
/// Consumes the installer's one-use configuration. The token reaches disk before the key is removed,
/// so a restart between those writes cannot enroll a second device or replace an existing identity.
/// </summary>
public sealed class EnrollmentService(HttpClient http, string stateDirectory, Func<string>? machineId = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task EnsureEnrolledAsync(CancellationToken ct)
    {
        var enrollmentPath = Path.Combine(stateDirectory, "enroll.json");
        if (!File.Exists(enrollmentPath))
        {
            return;
        }

        var settingsPath = Path.Combine(stateDirectory, "client.json");
        var settings = File.Exists(settingsPath)
            ? JsonNode.Parse(await File.ReadAllTextAsync(settingsPath, ct))?.AsObject()
                ?? throw new InvalidDataException("The client configuration was empty.")
            : new JsonObject();
        if (!string.IsNullOrWhiteSpace(settings["deviceToken"]?.GetValue<string>()))
        {
            File.Delete(enrollmentPath);
            return;
        }

        var enrollment = JsonSerializer.Deserialize<EnrollmentSettings>(await File.ReadAllTextAsync(enrollmentPath, ct), Json);
        if (enrollment is null || string.IsNullOrWhiteSpace(enrollment.EnrollmentKey)
            || !Uri.TryCreate(enrollment.ServerUrl, UriKind.Absolute, out var server)
            || (server.Scheme != Uri.UriSchemeHttps && server.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment))
        {
            throw new InvalidDataException("Enrollment needs a server URL and key.");
        }

        var identity = (machineId ?? ReadMachineId)();
        if (string.IsNullOrWhiteSpace(identity))
        {
            throw new InvalidDataException("The machine identity was empty.");
        }

        var version = typeof(EnrollmentService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var body = new EnrollmentRequest(enrollment.EnrollmentKey, Environment.MachineName, identity, enrollment.Action1EndpointId, version);
        var address = new Uri(new Uri(enrollment.ServerUrl.TrimEnd('/') + "/"), "api/v1/enroll");
        using var response = await http.PostAsJsonAsync(address, body, ct);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            // The response body may echo the key; neither exceptions nor the service log should contain it.
            throw new HttpRequestException("Enrollment was not accepted.", null, response.StatusCode);
        }

        var answer = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(ct);
        if (answer is null || string.IsNullOrWhiteSpace(answer.DeviceId) || string.IsNullOrWhiteSpace(answer.DeviceToken)
            || string.IsNullOrWhiteSpace(answer.DeviceName) || answer.Engines is null)
        {
            throw new InvalidDataException("The enrollment response was incomplete.");
        }

        settings["serverUrl"] = enrollment.ServerUrl.TrimEnd('/');
        settings["deviceToken"] = answer.DeviceToken;
        settings["refreshSeconds"] ??= 10;
        var temporaryPath = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // The new file inherits the directory's Users-read ACL, not enroll.json's private ACL.
            await File.WriteAllTextAsync(temporaryPath, settings.ToJsonString(Json), ct);
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        File.Delete(enrollmentPath);
    }

    private static string ReadMachineId()
    {
        if (OperatingSystem.IsWindows())
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string
                ?? throw new InvalidDataException("The Windows machine identity was not found.");
        }

        return File.ReadAllText("/etc/machine-id").Trim();
    }
}
