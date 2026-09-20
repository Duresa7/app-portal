using AppPortal.Server.Action1;
using AppPortal.Server.Admin;
using AppPortal.Server.Api;
using AppPortal.Server.Catalog;
using AppPortal.Server.Cli;
using AppPortal.Server.Data;
using AppPortal.Server.Devices;
using AppPortal.Server.Enrollment;
using AppPortal.Server.Installs;
using AppPortal.Server.Options;
using AppPortal.Server.Requests;
using AppPortal.Shared;

using Microsoft.Extensions.Options;

if (args.Length > 0 && args[0] == "healthcheck")
{
    // Used by the container HEALTHCHECK: the runtime image ships no curl.
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:8080";
    var port = new Uri(urls.Split(';')[0].Replace("+", "localhost").Replace("*", "localhost")).Port;
    try
    {
        using var reply = await probe.GetAsync($"http://localhost:{port}/healthz");
        return reply.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<Action1Options>().Bind(builder.Configuration.GetSection(Action1Options.Section));
builder.Services.AddOptions<PortalOptions>().Bind(builder.Configuration.GetSection(PortalOptions.Section));
builder.Services.AddOptions<DirectoryOptions>().Bind(builder.Configuration.GetSection(DirectoryOptions.Section));

builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<AdminStore>();
builder.Services.AddSingleton<AdminSessionStore>();
builder.Services.AddSingleton<LegacyImport>();
builder.Services.AddSingleton<CatalogStore>();
builder.Services.AddSingleton<DeviceStore>();
builder.Services.AddSingleton<EnrollmentKeyStore>();
builder.Services.AddSingleton<InstallStore>();
builder.Services.AddSingleton<AppRequestStore>();
builder.Services.AddSingleton<InstallService>();
builder.Services.AddHostedService<InstallStatusPoller>();

var action1 = builder.Configuration.GetSection(Action1Options.Section).Get<Action1Options>() ?? new Action1Options();
if (action1.IsFake)
{
    builder.Services.AddSingleton<IAction1Client, FakeAction1Client>();
}
else
{
    builder.Services.AddHttpClient<IAction1Client, Action1Client>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AppPortal.Server/0.1");
    });
}

// Directory sign-in is an add-on: with the section absent or off, no LDAP connection is ever opened and
// the stand-in below answers every caller. A section that is on but cannot work stops the server here
// rather than at somebody's first sign-in attempt.
var directory = builder.Configuration.GetSection(DirectoryOptions.Section).Get<DirectoryOptions>() ?? new DirectoryOptions();
directory.Validate();
if (directory.Enabled)
{
    if (!string.IsNullOrWhiteSpace(directory.CertificateFile))
    {
        if (!File.Exists(directory.CertificateFile))
        {
            throw new InvalidOperationException(
                $"Directory:CertificateFile is {directory.CertificateFile}, which does not exist. Mount the controller certificates there.");
        }

        OpenLdapEnvironment.PointAtCertificateFile(
            directory.CertificateFile,
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger("AppPortal.Server.Admin.Directory"));
    }

    builder.Services.AddSingleton<IDirectoryAuthenticator, LdapDirectoryAuthenticator>();
}
else
{
    builder.Services.AddSingleton<IDirectoryAuthenticator, DisabledDirectoryAuthenticator>();
}

builder.Services.AddSingleton<AdminSignIn>();

builder.Services.AddRazorPages();
builder.Services.AddAdminAuthentication();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

// Before anything reads a store, and before the CLI branches below: the database is the only place
// catalog, devices and history live, so a file that cannot be opened or migrated is fatal rather than
// something to serve around with empty data. Resolving the service happens inside the try because the
// constructor creates the data directory, and an unwritable volume refuses there before any migration.
Database? database = null;
try
{
    database = app.Services.GetRequiredService<Database>();
    database.Migrate();
    app.Services.GetRequiredService<LegacyImport>().Run();
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "The database at {Path} could not be opened or migrated",
        database?.Path ?? "the configured data directory");
    return 1;
}

if (args.Length > 0 && args[0] == "device")
{
    return DeviceCli.Run(args, app.Services.GetRequiredService<DeviceStore>(), Console.Out);
}

if (args.Length > 0 && args[0] == "admin")
{
    return AdminCli.Run(args, app.Services.GetRequiredService<AdminStore>(), app.Services.GetRequiredService<AdminSessionStore>(), Console.Out);
}

if (args.Length > 0 && args[0] == "key")
{
    return KeyCli.Run(args, app.Services.GetRequiredService<EnrollmentKeyStore>(), Console.Out);
}

if (args.Length > 0 && args[0] is "catalog" or "packages")
{
    return await CatalogCli.RunAsync(args, app.Services.GetRequiredService<CatalogStore>(), app.Services.GetRequiredService<IAction1Client>(), Console.Out, CancellationToken.None);
}

var options = app.Services.GetRequiredService<IOptions<Action1Options>>().Value;
app.Logger.LogInformation("Action1 mode: {Mode}; base URL {BaseUrl}; organization {Org}",
    options.IsFake ? "Fake" : "Live", options.BaseUrl, string.IsNullOrEmpty(options.OrgId) ? "(not set)" : "(set)");

if (app.Services.GetRequiredService<AdminStore>().None())
{
    app.Logger.LogWarning(
        "No administrator exists yet, so /admin cannot be signed in to. Create one with: " +
        "dotnet AppPortal.Server.dll admin add --username <name>");
}

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// The device bearer middleware guards the device API only. Admin JSON routes sit under the same
// /api/v1 prefix but authenticate with an apa_ token against the session table, so they are excluded
// here and guarded by the Admin policy instead.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments(ApiRoutes.Prefix)
               && !context.Request.Path.StartsWithSegments(AdminSessionApi.Prefix),
    branch => branch.UseMiddleware<DeviceAuthenticationMiddleware>());

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapPortalApi();
app.MapAdminSessionApi();
app.MapRazorPages();

app.Run();
return 0;

public partial class Program;
