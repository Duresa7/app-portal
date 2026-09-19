using AppPortal.Shared;

namespace AppPortal.Server.Devices;

/// <summary>Resolves the bearer token on API calls to a device record, or answers 401.</summary>
public sealed class DeviceAuthenticationMiddleware(RequestDelegate next, DeviceStore devices, ILogger<DeviceAuthenticationMiddleware> logger)
{
    private const string ItemKey = "AppPortal.Device";

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
        await next(context);
    }

    public static DeviceRecord Current(HttpContext context)
        => context.Items[ItemKey] as DeviceRecord
           ?? throw new InvalidOperationException("Device authentication did not run for this request.");
}
