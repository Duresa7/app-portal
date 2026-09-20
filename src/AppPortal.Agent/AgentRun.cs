using AppPortal.Shared;

namespace AppPortal.Agent;

/// <summary>
/// How the agent starts, whichever way it was started. Windows runs it as a service; a developer runs
/// it with <c>--console</c>, and CI with <c>--console --once</c> so a single heartbeat decides the exit
/// code. The service host is only added on Windows, so the project still builds and its tests still run
/// on the Linux half of the build matrix.
/// </summary>
public static class AgentRun
{
    public static async Task<int> MainAsync(string[] args)
    {
        var install = args.Contains("--install", StringComparer.OrdinalIgnoreCase);
        var uninstall = args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase);
        if (install || uninstall)
        {
            if (install && uninstall)
            {
                Console.Error.WriteLine("Choose either --install or --uninstall.");
                return 2;
            }

            return await ServiceRegistration.RunAsync(install);
        }

        var once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
        var console = args.Contains("--console", StringComparer.OrdinalIgnoreCase);
        var stateDirectory = Path.GetDirectoryName(PortalSettings.DefaultPath)!;
        Directory.CreateDirectory(stateDirectory);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        if (OperatingSystem.IsWindows() && !console)
        {
            builder.Services.AddWindowsService(options => options.ServiceName = ServiceRegistration.ServiceName);
        }

        builder.Logging.AddProvider(new AgentLog(Path.Combine(stateDirectory, "agent.log")));
        builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        builder.Services.AddSingleton<HeartbeatClient>();
        builder.Services.AddSingleton(provider => new HeartbeatWorker(
            provider.GetRequiredService<HeartbeatClient>(),
            provider.GetRequiredService<ILogger<HeartbeatWorker>>(),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            Path.Combine(stateDirectory, "agent.json"), once));
        builder.Services.AddHostedService(provider => provider.GetRequiredService<HeartbeatWorker>());
        using var host = builder.Build();
        await host.RunAsync();
        return host.Services.GetRequiredService<HeartbeatWorker>().ExitCode;
    }
}
