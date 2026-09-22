using System.Security.Claims;

using AppPortal.Server.Admin.Lists;
using AppPortal.Shared;

namespace AppPortal.Server.Admin.Api;

/// <summary>
/// The admin JSON API: the same store methods the Razor Pages call, offered over HTTP so the Windows
/// client can administer the portal without scraping pages. Every route here is a thin adapter. Nothing
/// in this folder may do something the web admin UI cannot, and the pages keep calling the stores
/// directly, so the two doors cannot drift into two behaviours.
/// </summary>
public static class AdminApi
{
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder app)
    {
        // One group, one policy, one log line per call. A route added to this group is guarded by
        // construction, which matters more here than anywhere else in the server: everything under it
        // can change the fleet, and a route that forgot its attribute would hand that to anyone.
        var group = app.MapGroup(AdminApiRoutes.Prefix)
            .RequireAuthorization(AdminAuth.Policy)
            .AddEndpointFilter<AdminApiLog>();

        group.MapAdminSessionEndpoints();
        group.MapAdminDashboardEndpoints();
        group.MapAdminInstallEndpoints();
        group.MapAdminRequestEndpoints();
        group.MapAdminCatalogEndpoints();
        group.MapAdminDeviceEndpoints();
        group.MapAdminKeyEndpoints();
        group.MapAdminAccountEndpoints();
        group.MapAdminSettingsEndpoints();

        return app;
    }

    /// <summary>The administrator this call authenticated as. The policy guarantees there is one.</summary>
    public static string AdminId(this HttpContext context)
        => context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";

    public static string Username(this HttpContext context)
        => context.User.FindFirst(ClaimTypes.Name)?.Value ?? "unknown";

    /// <summary>The session token this call carried, which is how a caller's own session is recognised.</summary>
    public static string? SessionToken(this HttpContext context)
        => context.User.FindFirst(AdminAuth.SessionTokenClaim)?.Value;

    /// <summary>
    /// The query string as the list filters read it. The filters from M1-12 own their own field names,
    /// so a filter is read here exactly as the page reads it and no name is written down twice.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> FilterValues(this HttpRequest request)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Query)
        {
            values[key] = value.FirstOrDefault();
        }

        return values;
    }

    /// <summary>
    /// The slice the caller asked for, bounded. A limit above the ceiling is cut to it rather than
    /// refused, and the answer says which limit was used, so a caller cannot ask for a whole fleet's
    /// history in one response and cannot be misled about how much of it they got.
    /// </summary>
    public static ListQuery ReadSlice(this HttpRequest request)
    {
        var limit = Number(request.Query["limit"], AdminApiLimits.DefaultLimit);
        var offset = Number(request.Query["offset"], 0);
        return new ListQuery(
            Math.Clamp(limit, 1, AdminApiLimits.MaxLimit),
            Math.Max(0, offset),
            Sort: null,
            WantTotal: true);
    }

    private static int Number(string? text, int fallback)
        => int.TryParse(text, out var parsed) ? parsed : fallback;

    /// <summary>
    /// An enum filter read by its member names and nothing else, in any case. Not Enum.TryParse: that
    /// also takes <c>1</c> as the second member and <c>Pending,Approved</c> as a flags value, and a
    /// caller who typed either did not mean the one status it happens to become.
    /// </summary>
    public static bool TryName<TEnum>(string text, out TEnum value) where TEnum : struct, Enum
    {
        var trimmed = text.Trim();
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (candidate.ToString().Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Turns a store's slice into the page a caller reads, projecting each row on the way.</summary>
    public static AdminPage<TOut> Page<TRow, TOut>(Slice<TRow> slice, ListQuery query, Func<TRow, TOut> project)
        => new([.. slice.Rows.Select(project)], slice.Offset, query.Limit ?? slice.Rows.Count, slice.HasMore, slice.Total);

    public static IResult Problem(int status, string message) => Results.Json(new ErrorMessage(message), statusCode: status);

    public static IResult NotFound(string message) => Problem(StatusCodes.Status404NotFound, message);

    public static IResult BadRequest(string message) => Problem(StatusCodes.Status400BadRequest, message);

    public static IResult Conflict(string message) => Problem(StatusCodes.Status409Conflict, message);
}

/// <summary>
/// One Information line per admin API call, naming the administrator and the route.
///
/// What is deliberately absent is the body, in either direction. Passwords, enrollment key plaintext
/// and device tokens all travel through these routes in a body, and a log is the one place a secret
/// ends up copied, shipped and kept. The query string is left out for the same reason it is left out
/// of the enrollment check: a filter can carry an account name, and the path alone says what was done.
/// </summary>
internal sealed class AdminApiLog(ILoggerFactory loggers) : IEndpointFilter
{
    /// <summary>Enough to read a route and its id, and not enough to flood the log from one request.</summary>
    private const int MaxRouteLength = 200;

    private readonly ILogger _logger = loggers.CreateLogger("AppPortal.Server.Admin.Api");

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        _logger.LogInformation("Administrator {Username} called {Method} {Route}",
            context.HttpContext.Username(), request.Method, Safe(request.Path.Value));
        return next(context);
    }

    /// <summary>
    /// The path reaches here decoded, so an id containing <c>%0A</c> would otherwise write a line of
    /// its own and let a caller forge log entries. Control characters become a dot and the rest is cut.
    /// </summary>
    private static string Safe(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        var chars = path[..Math.Min(path.Length, MaxRouteLength)].ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = '.';
            }
        }

        return new string(chars);
    }
}
