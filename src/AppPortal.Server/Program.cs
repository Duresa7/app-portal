using AppPortal.Server.Action1;
using AppPortal.Server.Api;
using AppPortal.Server.Catalog;
using AppPortal.Server.Cli;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Server.Options;
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

builder.Services.AddSingleton<CatalogStore>();
builder.Services.AddSingleton<DeviceStore>();
builder.Services.AddSingleton<InstallStore>();
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

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

if (args.Length > 0 && args[0] == "device")
{
    return DeviceCli.Run(args, app.Services.GetRequiredService<DeviceStore>(), Console.Out);
}

if (args.Length > 0 && args[0] is "catalog" or "packages")
{
    return await CatalogCli.RunAsync(args, app.Services.GetRequiredService<CatalogStore>(), app.Services.GetRequiredService<IAction1Client>(), Console.Out, CancellationToken.None);
}

var options = app.Services.GetRequiredService<IOptions<Action1Options>>().Value;
app.Logger.LogInformation("Action1 mode: {Mode}; base URL {BaseUrl}; organization {Org}",
    options.IsFake ? "Fake" : "Live", options.BaseUrl, string.IsNullOrEmpty(options.OrgId) ? "(not set)" : "(set)");

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.UseWhen(context => context.Request.Path.StartsWithSegments(ApiRoutes.Prefix), branch =>
    branch.UseMiddleware<DeviceAuthenticationMiddleware>());

app.MapPortalApi();

app.Run();
return 0;

public partial class Program;
