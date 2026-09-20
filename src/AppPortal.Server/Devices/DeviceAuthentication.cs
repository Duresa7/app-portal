using AppPortal.Shared;

namespace AppPortal.Server.Devices;

/// <summary>Resolves the bearer token on API calls to a device record, or answers 401.</summary>
public sealed class DeviceAuthenticationMiddleware(RequestDelegate next, DeviceStore devices, ILogger<DeviceAuthenticationMiddleware> logger)
{
    private const string ItemKey = "AppPortal.Device";
    private const string RequestedByKey = "RequestedBy";

    public async Task InvokeAsync(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        var device = token is null ? null : devices.Authenticate(token);
        if (device is null)
        {
            logger.LogInformation("Rejected API call from {Remote} without a valid device token", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new ErrorMessage("A valid device token is required."));
            return;
        }

        context.Items[ItemKey] = device;

        if (!TryReadRequester(context, out var requestedBy))
        {
            logger.LogInformation("Device {Device} sent an unusable {Header} header", device.Name, ApiHeaders.Requester);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ErrorMessage($"The {ApiHeaders.Requester} header contains characters that are not allowed."));
            return;
        }

        context.Items[RequestedByKey] = requestedBy;
        await next(context);
    }

    /// <summary>
    /// Reads the requester header. Absent or blank is success with null, so a client from before this
    /// header existed keeps working; present but carrying control characters is a refusal, because the
    /// value is written to the database and shown to an admin later.
    /// </summary>
    private static bool TryReadRequester(HttpContext context, out string? requestedBy)
    {
        requestedBy = null;
        if (!context.Request.Headers.TryGetValue(ApiHeaders.Requester, out var values))
        {
            return true;
        }

        var raw = values.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (raw.Any(char.IsControl))
        {
            return false;
        }

        var trimmed = raw.Trim();
        requestedBy = trimmed.Length > ApiHeaders.RequesterMaxLength
            ? trimmed[..ApiHeaders.RequesterMaxLength]
            : trimmed;
        return true;
    }

    /// <summary>The account the call was made for, or null when the client did not say.</summary>
    public static string? RequestedBy(HttpContext context)
        => context.Items.TryGetValue(RequestedByKey, out var value) ? value as string : null;

    public static DeviceRecord Current(HttpContext context)
        => context.Items[ItemKey] as DeviceRecord
           ?? throw new InvalidOperationException("Device authentication did not run for this request.");
}
