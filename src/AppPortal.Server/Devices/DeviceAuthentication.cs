using System.Collections.Concurrent;

using AppPortal.Shared;

namespace AppPortal.Server.Devices;

/// <summary>Resolves the bearer token on API calls to a device record, or answers 401.</summary>
public sealed class DeviceAuthenticationMiddleware(RequestDelegate next, DeviceStore devices, ILogger<DeviceAuthenticationMiddleware> logger)
{
    private const string ItemKey = "AppPortal.Device";
    private const string RequestedByKey = "RequestedBy";

    /// <summary>
    /// How stale "last seen" is allowed to get. A client polls every few seconds, and writing on every
    /// call would mean a database write per request for a column nobody reads to the second.
    /// </summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// When each device was last written, so the write happens about once a minute per device rather
    /// than once per request. One instance of this middleware serves the whole application, so this is
    /// per server rather than static; losing it on a restart costs one extra write per device.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTouched = new(StringComparer.Ordinal);

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
        TouchLastSeen(device);
        await next(context);
    }

    private void TouchLastSeen(DeviceRecord device)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = _lastTouched.GetOrAdd(device.Id, DateTimeOffset.MinValue);
        if (now - previous < TouchInterval)
        {
            return;
        }

        // Whoever wins this swap does the write; the others carry on and try again after the interval.
        if (!_lastTouched.TryUpdate(device.Id, now, previous))
        {
            return;
        }

        try
        {
            devices.TouchLastSeen(device.Id);
            device.LastSeenAt = now;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            // A heartbeat is not worth failing a request the device actually asked for.
            logger.LogWarning(ex, "Could not record last seen for device {Device}", device.Name);
        }
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
