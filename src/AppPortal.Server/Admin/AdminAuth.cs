using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace AppPortal.Server.Admin;

public static class AdminAuth
{
    public const string CookieScheme = "AdminCookie";
    public const string BearerScheme = "AdminBearer";
    public const string Policy = "Admin";

    /// <summary>Carries the session token on the signed-in principal so that sign-out can revoke it.</summary>
    public const string SessionTokenClaim = "app-portal:session";
}

/// <summary>The administrator behind the current request, for pages and endpoints that need to name them.</summary>
public interface IAdminContext
{
    string? AdminId { get; }
    string? Username { get; }
    bool IsSignedIn { get; }
}

public sealed class AdminContext(IHttpContextAccessor accessor) : IAdminContext
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public string? AdminId => User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public string? Username => User?.FindFirst(ClaimTypes.Name)?.Value;

    public bool IsSignedIn => AdminId is not null;
}

/// <summary>
/// Resolves <c>Authorization: Bearer apa_...</c> against the session table. Kept apart from the device
/// bearer middleware on purpose: that one guards <c>/api/v1/*</c> device routes and answers 401 to
/// anything it does not recognise, which is the wrong answer for an admin token.
/// </summary>
public sealed class AdminBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AdminSessionStore sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = header["Bearer ".Length..].Trim();
        if (!token.StartsWith("apa_", StringComparison.Ordinal))
        {
            // A device token on an admin route is not an admin: let the policy answer, not this handler.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var admin = sessions.Resolve(token, AdminSessionKind.Api);
        if (admin is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("The admin token is unknown, expired or revoked."));
        }

        var principal = AdminPrincipal.For(admin, token, AdminAuth.BearerScheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, AdminAuth.BearerScheme)));
    }

    /// <summary>
    /// The Admin policy names both schemes, so both are challenged and whichever writes last decides the
    /// answer. A browser asking for a page must get the cookie scheme's redirect to the sign-in form, not
    /// this scheme's 401, so this one stays quiet unless the request was for the admin JSON API.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Request.Path.StartsWithSegments(AdminSessionApi.Prefix))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = "Bearer";
        }

        return Task.CompletedTask;
    }
}

public static class AdminPrincipal
{
    public static ClaimsPrincipal For(AdminRecord admin, string sessionToken, string scheme)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, admin.Id),
                new Claim(ClaimTypes.Name, admin.Username),
                new Claim(AdminAuth.SessionTokenClaim, sessionToken),
            ],
            scheme);
        return new ClaimsPrincipal(identity);
    }
}

/// <summary>
/// Counts failed sign-ins per user name. Ten in fifteen minutes and the eleventh is refused without the
/// password being checked at all, so guessing costs an attacker time whether or not the account exists.
/// In memory by design: the window is short and a restart clearing it is not worth a table.
/// </summary>
public sealed class SignInThrottle
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.OrdinalIgnoreCase);

    public bool IsBlocked(string username)
    {
        var recent = Recent(username);
        return recent.Count >= MaxFailures;
    }

    public void RecordFailure(string username)
    {
        var list = _failures.GetOrAdd(username ?? "", _ => []);
        lock (list)
        {
            list.Add(DateTimeOffset.UtcNow);
        }
    }

    public void Clear(string username) => _failures.TryRemove(username ?? "", out _);

    private List<DateTimeOffset> Recent(string username)
    {
        if (!_failures.TryGetValue(username ?? "", out var list))
        {
            return [];
        }

        lock (list)
        {
            var cutoff = DateTimeOffset.UtcNow - Window;
            list.RemoveAll(at => at < cutoff);
            return [.. list];
        }
    }
}

public static class AdminAuthExtensions
{
    public static IServiceCollection AddAdminAuthentication(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IAdminContext, AdminContext>();
        services.AddSingleton<SignInThrottle>();

        services.AddAuthentication(AdminAuth.CookieScheme)
            .AddCookie(AdminAuth.CookieScheme, options =>
            {
                options.Cookie.Name = "AppPortal.Admin";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.LoginPath = "/admin/login";
                options.LogoutPath = "/admin/logout";
                options.AccessDeniedPath = "/admin/login";
                options.ExpireTimeSpan = AdminSessionStore.WebLifetime;
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    // An API client wants a status code, not a sign-in page.
                    if (context.Request.Path.StartsWithSegments(AdminSessionApi.Prefix))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = async context =>
                {
                    // The cookie is only half the answer. The session row is the other half, and deleting
                    // it is what makes sign-out and "disable this admin" take effect immediately.
                    var token = context.Principal?.FindFirst(AdminAuth.SessionTokenClaim)?.Value;
                    var sessions = context.HttpContext.RequestServices.GetRequiredService<AdminSessionStore>();
                    if (token is null || sessions.Resolve(token, AdminSessionKind.Web) is null)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(AdminAuth.CookieScheme);
                    }
                };
            })
            .AddScheme<AuthenticationSchemeOptions, AdminBearerHandler>(AdminAuth.BearerScheme, _ => { });

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminAuth.Policy, policy => policy
                .AddAuthenticationSchemes(AdminAuth.CookieScheme, AdminAuth.BearerScheme)
                .RequireAuthenticatedUser());

        return services;
    }
}

/// <summary>
/// The two session endpoints the Windows client's admin mode signs in with. M4-01 adds the rest of the
/// admin JSON API behind the same policy; these two are here because the session they issue is defined here.
/// </summary>
public static class AdminSessionApi
{
    /// <summary>Admin JSON routes, which the device bearer middleware must not guard.</summary>
    public const string Prefix = "/api/v1/admin";

    public sealed record SignInRequest(string Username, string Password);

    public sealed record SignInResponse(string Token, DateTimeOffset ExpiresAt);

    public static IEndpointRouteBuilder MapAdminSessionApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix);

        group.MapPost("/session", (
            SignInRequest body,
            AdminStore admins,
            AdminSessionStore sessions,
            SignInThrottle throttle,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("AppPortal.Server.Admin.Session");
            var username = (body?.Username ?? "").Trim();

            if (throttle.IsBlocked(username))
            {
                logger.LogWarning("Token request for {Username} refused: too many recent failures", username);
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var admin = admins.Verify(username, body?.Password ?? "");
            if (admin is null)
            {
                throttle.RecordFailure(username);
                logger.LogInformation("Token request failed for {Username}", username);
                return Results.Json(
                    new AppPortal.Shared.ErrorMessage("That user name and password do not match an enabled administrator."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.Clear(username);
            var token = sessions.Create(admin.Id, AdminSessionKind.Api);
            admins.RecordLogin(admin.Id);
            logger.LogInformation("Administrator {Username} took an API token", admin.Username);
            return Results.Ok(new SignInResponse(token, DateTimeOffset.UtcNow + AdminSessionStore.ApiLifetime));
        });

        group.MapDelete("/session", (HttpContext context, AdminSessionStore sessions) =>
        {
            sessions.Revoke(context.User.FindFirst(AdminAuth.SessionTokenClaim)?.Value);
            return Results.NoContent();
        }).RequireAuthorization(AdminAuth.Policy);

        return app;
    }
}
