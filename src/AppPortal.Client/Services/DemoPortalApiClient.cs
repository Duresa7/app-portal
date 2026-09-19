using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppPortal.Shared;

namespace AppPortal.Client.Services;

/// <summary>
/// Runs the whole interface with no server and no device registration, for looking at the app
/// on a machine that is not enrolled. Requested installs advance a step on each poll and then
/// appear in the inventory, so progress, history and the installed state all behave as they would live.
/// Nothing leaves the machine.
/// </summary>
public sealed class DemoPortalApiClient : IPortalApiClient
{
    private static readonly CatalogApp[] Catalog =
    [
        new("google-chrome", "Google Chrome", "Google LLC", "Web browser. Installs the current stable release from the Software Repository.", "Browsers", null, true),
        new("mozilla-firefox", "Mozilla Firefox", "Mozilla", "Web browser, 64-bit English (US) build.", "Browsers", null, false),
        new("7-zip", "7-Zip", "Igor Pavlov", "File archiver for zip, 7z, tar and other formats.", "Utilities", null, false),
        new("vlc", "VLC media player", "VideoLAN", "Plays most audio and video formats without extra codecs.", "Media", null, false),
        new("vscode", "Visual Studio Code", "Microsoft Corporation", "Source code editor. System-wide install for all users.", "Developer tools", null, true),
        new("notepadpp", "Notepad++", "Don Ho", "Plain text and source editor with tabs and syntax highlighting.", "Developer tools", null, false),
        new("obs", "OBS Studio", "OBS Project", "Screen recording and live streaming.", "Media", null, false),
        new("powertoys", "Microsoft PowerToys", "Microsoft Corporation", "Window layouts, a launcher, an image resizer and other utilities.", "Utilities", null, false),
    ];

    private readonly List<InstallRequest> _installs = [];
    private readonly List<InstalledApp> _installed =
    [
        new("Microsoft Edge", "Microsoft Corporation", "128.0.2739.42", null),
        new("NVIDIA App", "NVIDIA Corporation", "11.0.2.341", null),
        new("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Corporation", "14.40.33810", null),
        new("7-Zip 24.08 (x64)", "Igor Pavlov", "24.08", "7-zip"),
    ];

    private readonly Lock _gate = new();

    public DemoPortalApiClient()
    {
        _installs.Add(new InstallRequest(Guid.NewGuid().ToString("N"), "7-zip", "7-Zip", Environment.MachineName,
            DateTimeOffset.Now.AddHours(-26), DateTimeOffset.Now.AddHours(-26).AddMinutes(1),
            InstallState.Succeeded, 100, "The packages have been installed successfully."));
        _installs.Add(new InstallRequest(Guid.NewGuid().ToString("N"), "obs", "OBS Studio", Environment.MachineName,
            DateTimeOffset.Now.AddHours(-3), DateTimeOffset.Now.AddHours(-3).AddMinutes(4),
            InstallState.Failed, 0, "The installer returned exit code 1603."));
    }

    public Task<IReadOnlyList<CatalogApp>> GetCatalogAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CatalogApp>>(Catalog);

    public Task<DeviceInfo> GetDeviceAsync(CancellationToken ct)
        => Task.FromResult(new DeviceInfo($"{Environment.MachineName} (demo)", "demo-endpoint", "Connected", DateTimeOffset.Now));

    public Task<IReadOnlyList<InstalledApp>> GetInstalledAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<InstalledApp>>(_installed.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList());
        }
    }

    public Task<IReadOnlyList<InstallRequest>> GetInstallsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            for (var i = 0; i < _installs.Count; i++)
            {
                _installs[i] = Advance(_installs[i]);
            }

            return Task.FromResult<IReadOnlyList<InstallRequest>>(_installs.OrderByDescending(r => r.RequestedAt).ToList());
        }
    }

    public Task<InstallRequest> RequestInstallAsync(string appId, CancellationToken ct)
    {
        var app = Catalog.FirstOrDefault(a => a.Id == appId)
                  ?? throw new PortalApiException($"'{appId}' is not in the catalog.");

        var request = new InstallRequest(Guid.NewGuid().ToString("N"), app.Id, app.Name, Environment.MachineName,
            DateTimeOffset.Now, null, InstallState.Queued, 0, "Sent to the management service.");
        lock (_gate)
        {
            _installs.Add(request);
        }

        return Task.FromResult(request);
    }

    /// <summary>Moves one request along by elapsed time: queued for 3 s, installing for 9 s, then done.</summary>
    private InstallRequest Advance(InstallRequest request)
    {
        if (request.State is not (InstallState.Queued or InstallState.Running))
        {
            return request;
        }

        var age = DateTimeOffset.Now - request.RequestedAt;
        if (age < TimeSpan.FromSeconds(3))
        {
            return request with { State = InstallState.Queued, PercentComplete = 0, Detail = "Queued, waiting for the agent to pick up the job." };
        }

        if (age < TimeSpan.FromSeconds(12))
        {
            var percent = (int)Math.Clamp((age.TotalSeconds - 3) / 9 * 100, 5, 95);
            return request with { State = InstallState.Running, PercentComplete = percent, Detail = "Downloading and installing the package." };
        }

        if (!_installed.Any(a => a.CatalogAppId == request.AppId))
        {
            var app = Catalog.First(a => a.Id == request.AppId);
            _installed.Add(new InstalledApp(app.Name, app.Publisher, "1.0.0", app.Id));
        }

        return request with
        {
            State = InstallState.Succeeded,
            PercentComplete = 100,
            CompletedAt = DateTimeOffset.Now,
            Detail = "The packages have been installed successfully.",
        };
    }
}
