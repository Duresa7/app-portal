using AppPortal.Agent.Downloads;
using AppPortal.Agent.Enrollment;
using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Agent.Sessions;
using AppPortal.Agent.Update;
using AppPortal.Shared;

namespace AppPortal.Agent;

/// <summary>
/// How the agent starts, whichever way it was started. Windows runs it as a service; a developer runs
/// it with <c>--console</c>, and CI with <c>--console --once</c> so a single heartbeat decides the exit
/// code and with <c>--check</c> to prove the release feed still parses. The service host is only added
/// on Windows, so the project still builds and its tests still run on the Linux half of the build matrix.
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

        if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
        {
            return await UpdateCheck.RunAsync(CancellationToken.None);
        }

        var once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
        var console = args.Contains("--console", StringComparer.OrdinalIgnoreCase);
        var stateDirectory = Path.GetDirectoryName(PortalSettings.ResolvedPath)!;
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
        builder.Services.AddSingleton(provider => new EnrollmentService(provider.GetRequiredService<HttpClient>(), stateDirectory));
        builder.Services.AddSingleton(provider => new HeartbeatWorker(
            provider.GetRequiredService<HeartbeatClient>(),
            provider.GetRequiredService<ILogger<HeartbeatWorker>>(),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            Path.Combine(stateDirectory, "agent.json"), once, provider.GetRequiredService<EnrollmentService>()));
        builder.Services.AddHostedService(provider => provider.GetRequiredService<HeartbeatWorker>());
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        // Away from Windows there are no sessions to run in, so every per-user install parks rather
        // than running somewhere it should not.
        builder.Services.AddSingleton<IUserSessionLauncher>(provider => OperatingSystem.IsWindows()
            ? new WindowsUserSessions(provider.GetRequiredService<ILogger<WindowsUserSessions>>())
            : new NoUserSessions());
        builder.Services.AddSingleton<IUninstallRegistry>(_ => OperatingSystem.IsWindows()
            ? new WindowsUninstallRegistry()
            : new NoUninstallRegistry());
        builder.Services.AddSingleton(provider => new SoftwareReporter(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<ILogger<SoftwareReporter>>(),
            provider.GetRequiredService<IUserSessionLauncher>()));
        builder.Services.AddSingleton(provider => new InstallerCache(
            Path.Combine(stateDirectory, "downloads"), provider.GetRequiredService<ILogger<InstallerCache>>()));
        builder.Services.AddSingleton(provider => new ResumableDownload(
            // No timeout: a several-gigabyte download over an office connection outlasts any sensible one.
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            provider.GetRequiredService<InstallerCache>(),
            provider.GetRequiredService<ILogger<ResumableDownload>>()));
        builder.Services.AddSingleton<IPackageExecutor>(provider => new DirectInstallerExecutor(
            provider.GetRequiredService<ResumableDownload>(),
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<ILogger<DirectInstallerExecutor>>(),
            stateDirectory,
            provider.GetRequiredService<IUserSessionLauncher>(),
            provider.GetRequiredService<IUninstallRegistry>()));
        builder.Services.AddSingleton<IPackageExecutor>(provider => new WingetExecutor(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<ILogger<WingetExecutor>>(),
            stateDirectory,
            provider.GetRequiredService<IUserSessionLauncher>()));
        builder.Services.AddSingleton<IPackageManagerLocator>(_ => OperatingSystem.IsWindows()
            ? new WindowsPackageManagerLocator()
            : new NoPackageManagerLocator());
        builder.Services.AddSingleton<IPackageExecutor>(provider => new ManagedPackageExecutor(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IUserSessionLauncher>(),
            provider.GetRequiredService<IPackageManagerLocator>(),
            provider.GetRequiredService<ILogger<ManagedPackageExecutor>>(),
            stateDirectory));
        if (!once)
        {
            // Not in a single-shot run: the job loop long-polls for twenty-five seconds, and a run whose
            // only purpose is one heartbeat should not wait that out before it can exit.
            builder.Services.AddHostedService(provider => new JobRunner(
                provider.GetRequiredService<HttpClient>(),
                provider.GetServices<IPackageExecutor>(),
                provider.GetRequiredService<ILogger<JobRunner>>(),
                software: provider.GetRequiredService<SoftwareReporter>(),
                sessions: provider.GetRequiredService<IUserSessionLauncher>()));
            // On start and once a day: what the catalog page counts when an administrator chooses a
            // package manager for an app.
            builder.Services.AddHostedService(provider => new ManagerReporter(
                provider.GetRequiredService<HttpClient>(),
                provider.GetRequiredService<IProcessRunner>(),
                provider.GetRequiredService<IPackageManagerLocator>(),
                provider.GetRequiredService<ILogger<ManagerReporter>>(),
                provider.GetRequiredService<IUserSessionLauncher>()));
            // Every start, because a restart and an upgrade both end in one, and those are the two
            // moments the server's picture of this device is otherwise wrong.
            builder.Services.AddHostedService(provider => new StartupSoftwareSweep(
                provider.GetRequiredService<SoftwareReporter>(),
                provider.GetRequiredService<ILogger<StartupSoftwareSweep>>()));
            // Same reason: the update loop starts by asking GitHub what the newest release is, and a
            // run whose only purpose is one heartbeat has no business downloading anything.
            builder.Services.AddHostedService(provider =>
            {
                var updatePaths = new UpdatePaths(AppContext.BaseDirectory, stateDirectory);
                var feed = new GitHubReleaseFeed(
                    provider.GetRequiredService<HttpClient>(),
                    UpdateRepository.Resolve(PortalSettings.Load().UpdateRepository));
                var downloads = new HttpUpdateDownloader(
                    // A release MSI over an office connection outlasts the thirty seconds an API call gets.
                    new HttpClient { Timeout = TimeSpan.FromMinutes(30) },
                    updatePaths,
                    provider.GetRequiredService<ILogger<HttpUpdateDownloader>>());
                var update = new SelfUpdate(
                    feed,
                    downloads,
                    provider.GetRequiredService<IProcessRunner>(),
                    new InstalledClientPresence(updatePaths.InstallDir),
                    updatePaths,
                    provider.GetRequiredService<ILogger<SelfUpdate>>());
                return new UpdateWorker(
                    update,
                    updatePaths,
                    provider.GetRequiredService<IProcessRunner>(),
                    provider.GetRequiredService<IUninstallRegistry>(),
                    provider.GetRequiredService<ILogger<UpdateWorker>>());
            });
        }
        using var host = builder.Build();
        // Take the worker before the run. RunAsync disposes the host on shutdown, so asking the provider
        // for it afterwards throws instead of reporting the exit code CI reads.
        var worker = host.Services.GetRequiredService<HeartbeatWorker>();
        await host.RunAsync();
        return worker.ExitCode;
    }
}
