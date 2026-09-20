using System.Threading.RateLimiting;

using AppPortal.Server.Devices;
using AppPortal.Shared;

using Microsoft.AspNetCore.RateLimiting;

namespace AppPortal.Server.Enrollment;

/// <summary>
/// The one route a PC may call before it has a token. It trades an enrollment key for a device record
/// and the device token that every other API call needs, so an installer never has to carry a token of
/// its own: the key is the only secret that travels with the package, it can be revoked, and it is
/// worth nothing once it has expired or run out of uses.
/// </summary>
public static class EnrollmentEndpoints
{
    /// <summary>Named policy on the two enrollment routes.</summary>
    public const string RateLimitPolicy = "enroll";

    /// <summary>
    /// Attempts allowed per minute from one address. Enrollment is a once-per-machine event, so this is
    /// generous for a rollout and mean for anyone working through a key space one guess at a time.
    /// </summary>
    public const int PerMinutePerSource = 30;

    public static IServiceCollection AddEnrollmentRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new ErrorMessage("Too many enrollment attempts from this address. Try again in a minute."),
                    token);
            };

            options.AddPolicy<string>(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                Source(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PerMinutePerSource,
                    Window = TimeSpan.FromMinutes(1),
                    // Refuse rather than hold: an installer waiting on a queue slot looks like a hang.
                    QueueLimit = 0,
                }));
        });

        return services;
    }

    public static IEndpointRouteBuilder MapEnrollmentApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiRoutes.Enroll).RequireRateLimiting(RateLimitPolicy);

        group.MapPost("/", (
            HttpContext context,
            EnrollRequest? body,
            EnrollmentKeyStore keys,
            EnrollmentEventStore events,
            DeviceStore devices,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("AppPortal.Server.Enrollment");
            var source = Source(context);

            // Looked up before anything is spent, so a refusal can still name the key it was made with.
            // A key nobody has ever created gives null here, and the attempt is recorded against no key.
            var presented = keys.FindByPlaintext(body?.Key);

            if (body is null
                || string.IsNullOrWhiteSpace(body.Key)
                || string.IsNullOrWhiteSpace(body.DeviceName)
                || string.IsNullOrWhiteSpace(body.MachineId))
            {
                events.Record(presented?.Id, null, source, EnrollmentOutcome.Rejected);
                logger.LogInformation("Enrollment from {Source} refused: the request was incomplete", source);
                return Results.BadRequest(new ErrorMessage("key, deviceName and machineId are all required."));
            }

            // Spending the key is the authentication step, and it is one statement so that two machines
            // racing for a key's last use cannot both win. Everything after this point has a key behind it.
            var key = keys.TryConsume(body.Key);
            if (key is null)
            {
                events.Record(presented?.Id, null, source, EnrollmentOutcome.KeyRefused);
                logger.LogInformation("Enrollment from {Source} refused: the key is not usable", source);
                return Results.Json(
                    new ErrorMessage("That enrollment key is not usable."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var grantAgent = key.DefaultEngine is EnrollmentEngine.Agent or EnrollmentEngine.Both;
            var needsAction1 = key.DefaultEngine is EnrollmentEngine.Action1 or EnrollmentEngine.Both;
            if (needsAction1 && string.IsNullOrWhiteSpace(body.Action1EndpointId))
            {
                events.Record(key.Id, null, source, EnrollmentOutcome.Rejected);
                logger.LogInformation("Enrollment of {Device} from {Source} refused: no Action1 endpoint", body.DeviceName, source);
                return Results.BadRequest(new ErrorMessage(
                    $"The key '{key.Name}' enrolls devices for {EnrollmentKeyStore.Name(key.DefaultEngine)}, "
                    + "so action1EndpointId is required."));
            }

            // A device an administrator disabled stays disabled. Re-enrolling is otherwise a way for
            // anyone at the keyboard to undo that, and a token that authenticates nothing would be a
            // worse answer than a clear refusal.
            var known = devices.FindByMachineId(body.MachineId) ?? devices.FindByName(body.DeviceName);
            if (known is { Enabled: false })
            {
                events.Record(key.Id, known.Id, source, EnrollmentOutcome.DeviceDisabled);
                logger.LogWarning("Enrollment of the disabled device {Device} from {Source} refused", known.Name, source);
                return Results.Json(
                    new ErrorMessage("This device is disabled. An administrator has to enable it before it can enroll."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            EnrollmentResult result;
            try
            {
                result = devices.Enroll(
                    body.MachineId,
                    body.DeviceName,
                    body.Action1EndpointId,
                    grantAgent,
                    body.AgentVersion,
                    key.Id);
            }
            catch (DeviceRejectedException ex)
            {
                events.Record(key.Id, known?.Id, source, EnrollmentOutcome.Rejected);
                logger.LogInformation("Enrollment of {Device} from {Source} refused: {Reason}", body.DeviceName, source, ex.Message);
                return Results.Json(new ErrorMessage(ex.Message), statusCode: StatusCodes.Status409Conflict);
            }

            var outcome = result.Existing ? EnrollmentOutcome.ReEnrolled : EnrollmentOutcome.Enrolled;
            events.Record(key.Id, result.Device.Id, source, outcome);
            logger.LogInformation(
                "Device {Device} {Outcome} from {Source} with key {Key}",
                result.Device.Name, EnrollmentEventStore.Name(outcome), source, key.Name);

            return Results.Json(
                new EnrollResponse(result.Device.Id, result.Token, result.Device.Name, Engines(result.Device)),
                statusCode: StatusCodes.Status201Created);
        });

        // The setup wizard asks this before it runs the installer, so a key that is already spent is
        // caught while somebody is still standing at the machine. It spends nothing, and it is not
        // written to the audit trail: a wizard that retries would bury the attempts that matter.
        group.MapGet("/check", (HttpContext context, EnrollmentKeyStore keys) =>
        {
            var presented = context.Request.Headers[ApiHeaders.EnrollmentKey].ToString();
            var key = keys.FindByPlaintext(presented);
            return key?.Status == EnrollmentKeyStatus.Active
                ? Results.NoContent()
                : Results.Json(
                    new ErrorMessage("That enrollment key is not usable."),
                    statusCode: StatusCodes.Status401Unauthorized);
        });

        return app;
    }

    /// <summary>What the device can install through, now that it has enrolled.</summary>
    private static List<string> Engines(DeviceRecord device)
    {
        var engines = new List<string>(2);
        if (device.HasAction1)
        {
            engines.Add("action1");
        }

        if (device.HasAgent)
        {
            engines.Add("agent");
        }

        return engines;
    }

    /// <summary>
    /// Who the attempt came from, for the rate limiter's partition and the audit trail. Behind a reverse
    /// proxy this is the proxy unless forwarded headers are configured, which is the deployment's call
    /// to make rather than something to guess at here.
    /// </summary>
    private static string Source(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
