using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Shared;

namespace AppPortal.Client.Services;

/// <summary>
/// The admin API with no server behind it, for --demo. One account, <c>admin</c> with the password
/// <c>demo</c>, over a small fleet held in memory. Changes stick for as long as the window is open and
/// vanish with it. Nothing leaves the machine.
/// </summary>
public sealed class DemoAdminApiClient : IAdminApiClient
{
    public const string DemoUsername = "admin";
    public const string DemoPassword = "demo";

    /// <summary>Shaped like a real one so nothing downstream has to know it is not.</summary>
    private const string DemoToken = "apa_demo";

    private readonly Lock _gate = new();
    private readonly List<AdminDevice> _devices = [];
    private readonly List<AdminInstall> _installs = [];
    private readonly List<AdminRequest> _requests = [];
    private readonly List<AdminCatalogApp> _catalog = [];
    private readonly List<EnrollmentKeySummary> _keys = [];
    private readonly List<AdminAccount> _admins = [];
    private readonly List<EnrollmentKeyEvent> _keyEvents = [];
    private readonly Dictionary<string, IReadOnlyList<DeviceManager>> _managers = [];
    private AdminSettings _settings = new(EngineLabel.Agent);

    public DemoAdminApiClient()
    {
        var now = DateTimeOffset.Now;

        _admins.Add(new AdminAccount("adm-1", DemoUsername, false, "local", now.AddDays(-120), now.AddMinutes(-2)));
        _admins.Add(new AdminAccount("adm-2", "helpdesk", false, "local", now.AddDays(-40), now.AddDays(-1)));
        _admins.Add(new AdminAccount("adm-3", "contractor", true, "local", now.AddDays(-200), now.AddDays(-95)));

        _keys.Add(new EnrollmentKeySummary("key-1", "Head office rollout", "7Kq2mXa9", EngineLabel.Agent, "active",
            now.AddDays(30), 50, 4, null, DemoUsername, now.AddDays(-14)));
        _keys.Add(new EnrollmentKeySummary("key-2", "Warehouse tablets", "Vd91pQe4", EngineLabel.Action1, "revoked",
            null, null, 2, now.AddDays(-3), "helpdesk", now.AddDays(-60)));

        AddDevice("dev-1", "RECEPTION-01", true, "1.0.0", now.AddMinutes(-1), now.AddDays(-14));
        AddDevice("dev-2", "FINANCE-LT-04", true, "1.0.0", now.AddMinutes(-6), now.AddDays(-13));
        AddDevice("dev-3", "DESIGN-WS-02", true, "1.0.0", now.AddHours(-2), now.AddDays(-9));
        AddDevice("dev-4", "WAREHOUSE-TAB-1", false, null, now.AddDays(-2), now.AddDays(-58));
        AddDevice("dev-5", Environment.MachineName, true, "1.0.0", now, now.AddDays(-1));
        SeedFleetAdministration(now);

        AddApp("google-chrome", "Google Chrome", "Google LLC", "Browsers", featured: true);
        AddApp("mozilla-firefox", "Mozilla Firefox", "Mozilla", "Browsers");
        AddApp("7-zip", "7-Zip", "Igor Pavlov", "Utilities");
        AddApp("vscode", "Visual Studio Code", "Microsoft Corporation", "Developer tools", featured: true);
        AddApp("obs", "OBS Studio", "OBS Project", "Media");
        AddApp("legacy-vpn", "Legacy VPN client", "Contoso", "Networking", hidden: true);
        AddCatalogSources();

        // Today, this week and running now, so every tile on the dashboard has something to count.
        AddInstall("dev-1", "google-chrome", InstallState.Succeeded, now.AddHours(-1), 100, "The packages have been installed successfully.");
        AddInstall("dev-2", "vscode", InstallState.Running, now.AddMinutes(-3), 45, "Downloading and installing the package.");
        AddInstall("dev-3", "obs", InstallState.Failed, now.AddDays(-2), 0, "The installer returned exit code 1603.");
        AddInstall("dev-5", "7-zip", InstallState.Succeeded, now.AddHours(-26), 100, "The packages have been installed successfully.");
        AddInstall("dev-1", "mozilla-firefox", InstallState.Queued, now.AddMinutes(-1), 0, "Queued, waiting for the agent to pick up the job.");
        AddInstall("dev-2", "7-zip", InstallState.Succeeded, now.AddDays(-5), 100, "The packages have been installed successfully.");

        _requests.Add(new AdminRequest("req-1", "Slack, for the new support rota", "RECEPTION-01", @"CONTOSO\alee",
            AppRequestStatus.Pending, null, null, now.AddHours(-5), null));
        _requests.Add(new AdminRequest("req-2", "Blender, for the product renders", "DESIGN-WS-02", @"CONTOSO\mjones",
            AppRequestStatus.Pending, null, null, now.AddDays(-1), null));
        _requests.Add(new AdminRequest("req-3", "Notepad++, for editing config files", "FINANCE-LT-04", @"CONTOSO\pkaur",
            AppRequestStatus.Approved, "Added to the catalog, it should appear within the hour.", DemoUsername, now.AddDays(-4), now.AddDays(-3)));
        _requests.Add(new AdminRequest("req-4", "A licence for the full Acrobat", "FINANCE-LT-04", @"CONTOSO\pkaur",
            AppRequestStatus.Denied, "We have no spare licences this quarter.", "helpdesk", now.AddDays(-9), now.AddDays(-8)));

        AddInstallAndRequestHistory(now);
    }

    public string? Token { get; set; }

    public event EventHandler? Unauthorized;

    public Task<AdminSignedIn> SignInAsync(string username, string password, string? deviceName, CancellationToken ct)
    {
        if (!string.Equals(username.Trim(), DemoUsername, StringComparison.OrdinalIgnoreCase) || password != DemoPassword)
        {
            throw new PortalApiException(
                $"That user name and password do not match an enabled administrator. In demo mode, sign in as {DemoUsername} with the password {DemoPassword}.",
                HttpStatusCode.Unauthorized);
        }

        return Task.FromResult(new AdminSignedIn(DemoToken, DateTimeOffset.UtcNow.AddDays(30)));
    }

    public Task SignOutAsync(CancellationToken ct) => Guarded(() => 0);

    public Task<IReadOnlyList<AdminSessionSummary>> GetSessionsAsync(CancellationToken ct)
        => Guarded<IReadOnlyList<AdminSessionSummary>>(() =>
        [
            new("ses-1", Environment.MachineName, "api", DateTimeOffset.Now.AddMinutes(-2), DateTimeOffset.Now.AddDays(30), DateTimeOffset.Now, true),
        ]);

    public Task RevokeSessionAsync(string id, CancellationToken ct) => Guarded(() => 0);

    public Task<DashboardCounts> GetDashboardAsync(CancellationToken ct)
        => Guarded(() =>
        {
            var today = new DateTimeOffset(DateTime.Today);
            var weekAgo = DateTimeOffset.Now.AddDays(-7);
            return new DashboardCounts(
                _devices.Count,
                _installs.Count(i => i.RequestedAt >= today),
                _installs.Count(i => i.State == InstallState.Failed && i.RequestedAt >= weekAgo),
                _installs.Count(i => i.State is InstallState.Queued or InstallState.Running),
                _requests.Count(r => r.Status == AppRequestStatus.Pending));
        });

    public Task<AdminPage<AdminInstall>> GetInstallsAsync(AdminInstallFilter filter, int offset, int limit, CancellationToken ct)
        => Guarded(() => Page(_installs
            // The whole name, as the server matches it: the web page picks a device from a list, not by typing part of one.
            .Where(i => filter.Device is null || string.Equals(i.DeviceName, filter.Device, StringComparison.OrdinalIgnoreCase))
            .Where(i => filter.AppId is null || string.Equals(i.AppId, filter.AppId, StringComparison.OrdinalIgnoreCase))
            .Where(i => filter.State is null || i.State == filter.State)
            .Where(i => filter.Requester is null || (i.RequestedBy ?? "").Contains(filter.Requester, StringComparison.OrdinalIgnoreCase))
            .Where(i => filter.From is null || DateOnly.FromDateTime(i.RequestedAt.LocalDateTime) >= filter.From)
            .Where(i => filter.To is null || DateOnly.FromDateTime(i.RequestedAt.LocalDateTime) <= filter.To)
            .Where(i => !filter.AwaitingRestart || i.RebootState == RebootState.Pending)
            .OrderByDescending(i => i.RequestedAt), offset, limit));

    public Task<AdminInstall> GetInstallAsync(string id, CancellationToken ct)
        => Guarded(() => Find(_installs, i => i.Id == id, "No such install."));

    public Task<AdminInstall> CancelInstallAsync(string id, CancellationToken ct)
        => Guarded(() =>
        {
            // The server's rules and words, so the page meets the same refusals here as it would there.
            var install = Find(_installs, i => i.Id == id, "No such install.");
            if (install.State is not (InstallState.Queued or InstallState.Running))
            {
                throw new PortalApiException($"{install.AppName} has already finished, so there is nothing to stop.", HttpStatusCode.Conflict);
            }

            if (install.Engine != EngineLabel.Agent)
            {
                throw new PortalApiException($"{install.AppName} is being installed by Action1 and has to be stopped there.", HttpStatusCode.Conflict);
            }

            return Replace(_installs, install, install with
            {
                State = InstallState.Cancelled,
                CompletedAt = DateTimeOffset.Now,
                Detail = $"Stopped by {DemoUsername}.",
            });
        });

    public Task<AdminPage<AdminRequest>> GetRequestsAsync(AppRequestStatus? status, int offset, int limit, CancellationToken ct)
        => Guarded(() => Page(_requests.Where(r => status is null || r.Status == status).OrderByDescending(r => r.CreatedAt), offset, limit));

    public Task<AdminRequest> ApproveRequestAsync(string id, string? reason, CancellationToken ct)
        => Guarded(() => Decide(id, AppRequestStatus.Approved, reason));

    public Task<AdminRequest> DenyRequestAsync(string id, string? reason, CancellationToken ct)
        => Guarded(() => Decide(id, AppRequestStatus.Denied, reason));

    public Task<AdminPage<AdminCatalogApp>> GetCatalogAsync(string? search, int offset, int limit, CancellationToken ct)
        => Guarded(() => Page(_catalog
            .Where(a => string.IsNullOrWhiteSpace(search)
                        || a.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                        || a.Id.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                        || a.Publisher.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase), offset, limit));

    public Task<AdminCatalogApp> GetCatalogAppAsync(string id, CancellationToken ct)
        => Guarded(() => FindApp(id));

    public Task<AdminCatalogApp> SaveCatalogAppAsync(AdminCatalogApp app, CancellationToken ct)
        => Guarded(() =>
        {
            ValidateApp(app);
            UpsertApp(app);
            return app;
        });

    public Task DeleteCatalogAppAsync(string id, CancellationToken ct)
        => Guarded(() =>
        {
            var app = FindApp(id);
            if (_installs.Any(i => string.Equals(i.AppId, app.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PortalApiException(
                    $"'{app.Id}' cannot be deleted because installs refer to it. Hide it instead to take it off devices and keep the history.",
                    HttpStatusCode.Conflict);
            }

            _catalog.Remove(app);
            return 0;
        });

    public Task<AdminCatalogApp> SetCatalogAppHiddenAsync(string id, bool hidden, CancellationToken ct)
        => Guarded(() =>
        {
            var app = FindApp(id);
            return Replace(_catalog, app, app with { Hidden = hidden });
        });

    public Task<AdminCatalogImported> ImportCatalogAsync(string catalogJson, CancellationToken ct)
        => Guarded(() =>
        {
            DemoCatalogFile? file;
            try
            {
                file = JsonSerializer.Deserialize<DemoCatalogFile>(catalogJson, CatalogJson);
            }
            catch (JsonException ex)
            {
                throw new PortalApiException("That file is not valid JSON. " + ex.Message, HttpStatusCode.BadRequest);
            }
            catch (NotSupportedException)
            {
                throw new PortalApiException($"An agent definition needs a kind of {PackageDefinition.Kinds}.", HttpStatusCode.BadRequest);
            }

            // Every app checked before any is written, as the server does, so a bad file changes nothing.
            var apps = file?.Apps ?? [];
            foreach (var app in apps)
            {
                ValidateApp(app);
            }

            foreach (var app in apps)
            {
                UpsertApp(app with { Requires = app.Requires ?? [], Action1 = app.Action1 ?? new AdminAction1Package("") });
            }

            return new AdminCatalogImported(apps.Count);
        });

    public Task<string> ExportCatalogAsync(CancellationToken ct)
        => Guarded(() => JsonSerializer.Serialize(new DemoCatalogFile(_catalog), CatalogJson));

    public Task<IReadOnlyList<AdminPackageResult>> SearchAction1PackagesAsync(string term, CancellationToken ct)
        => Guarded<IReadOnlyList<AdminPackageResult>>(() =>
        [
            .. new[]
            {
                new AdminPackageResult("Google_Chrome", "Google Chrome", "Google LLC", true),
                new AdminPackageResult("Mozilla_Firefox", "Mozilla Firefox", "Mozilla", true),
                new AdminPackageResult("Zoom_Workplace", "Zoom Workplace", "Zoom Video Communications", true),
            }.Where(p => string.IsNullOrWhiteSpace(term) || p.Name.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase)),
        ]);

    public Task<AdminPackageVerified> VerifyAction1PackageAsync(AdminPackageRef package, CancellationToken ct)
        => Guarded(() => new AdminPackageVerified(true, $"Resolved to version {(string.IsNullOrWhiteSpace(package.Version) || package.Version == "latest" ? "1.0.0" : package.Version)}."));

    public Task<AdminInstallerHash> HashInstallerAsync(string url, CancellationToken ct)
        => Guarded(() => new AdminInstallerHash(new string('0', 64), 52_428_800));

    public Task<AdminWingetLookup> LookupWingetAsync(AdminPackageRef package, CancellationToken ct)
        => Guarded(() => new AdminWingetLookup(null, "Demo mode does not look packages up. On a real server this asks winget."));

    public Task<AdminPage<AdminDevice>> GetDevicesAsync(string? search, int offset, int limit, CancellationToken ct)
        => Guarded(() => Page(_devices
            .Where(d => string.IsNullOrWhiteSpace(search) || d.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase), offset, limit));

    public Task<AdminDeviceDetail> GetDeviceAsync(string id, CancellationToken ct)
        => Guarded(() =>
        {
            var device = Find(_devices, d => d.Id == id, "No such device.");
            return new AdminDeviceDetail(
                device,
                [.. _installs.Where(i => i.DeviceId == id).OrderByDescending(i => i.RequestedAt).Take(10)],
                [.. _requests.Where(r => r.DeviceName == device.Name).OrderByDescending(r => r.CreatedAt).Take(10)],
                _managers.TryGetValue(id, out var managers) ? managers : []);
        });

    public Task<AdminDeviceToken> CreateDeviceAsync(AdminDeviceCreate device, CancellationToken ct)
        => Guarded(() =>
        {
            var name = device.Name.Trim();
            if (name.Length == 0)
            {
                throw new PortalApiException("A device needs a name.", HttpStatusCode.BadRequest);
            }

            if (_devices.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PortalApiException($"A device called '{name}' is already registered. Rotate its token instead.", HttpStatusCode.Conflict);
            }

            var id = "dev-" + Guid.NewGuid().ToString("N")[..8];
            _devices.Add(new AdminDevice(id, name, device.Action1EndpointId ?? "", true, false, null, null, null, null, null,
                DateTimeOffset.Now, null, 0));
            return new AdminDeviceToken(id, DemoDeviceToken());
        });

    public Task<AdminDevice> UpdateDeviceAsync(string id, AdminDeviceUpdate update, CancellationToken ct)
        => Guarded(() =>
        {
            var device = Find(_devices, d => d.Id == id, "No such device.");
            var name = update.Name.Trim();
            if (name.Length == 0)
            {
                throw new PortalApiException("A device needs a name.", HttpStatusCode.Conflict);
            }

            if (_devices.Any(d => d.Id != id && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PortalApiException($"Another device is already called '{name}'.", HttpStatusCode.Conflict);
            }

            return Replace(_devices, device, device with
            {
                Name = name,
                EndpointId = update.Action1EndpointId ?? device.EndpointId,
                Enabled = update.Enabled,
                EnginePreference = update.EnginePreference,
            });
        });

    public Task<AdminDeviceToken> RotateDeviceTokenAsync(string id, CancellationToken ct)
        => Guarded(() => new AdminDeviceToken(Find(_devices, d => d.Id == id, "No such device.").Id, DemoDeviceToken()));

    public Task DeleteDeviceAsync(string id, CancellationToken ct)
        => Guarded(() =>
        {
            var device = Find(_devices, d => d.Id == id, "No such device.");
            if (_installs.Any(i => i.DeviceId == id && i.State is InstallState.Queued or InstallState.Running))
            {
                throw new PortalApiException("This device has an install still running. Wait for it to finish, or disable the device instead.", HttpStatusCode.Conflict);
            }

            _devices.Remove(device);
            return 0;
        });

    public Task<AdminPage<EnrollmentKeySummary>> GetKeysAsync(int offset, int limit, CancellationToken ct)
        => Guarded(() => Page(_keys.OrderByDescending(k => k.CreatedAt), offset, limit));

    public Task<EnrollmentKeySummary> GetKeyAsync(string id, CancellationToken ct)
        => Guarded(() => Find(_keys, k => k.Id == id, "No such enrollment key."));

    public Task<EnrollmentKeyCreated> CreateKeyAsync(EnrollmentKeyCreate key, CancellationToken ct)
        => Guarded(() =>
        {
            if (string.IsNullOrWhiteSpace(key.Name))
            {
                throw new PortalApiException("An enrollment key needs a name.", HttpStatusCode.BadRequest);
            }

            // Visibly a sample, so nobody mistakes it for a key that would enroll anything.
            var plaintext = "ape_demo" + Guid.NewGuid().ToString("N")[..16];
            var summary = new EnrollmentKeySummary("key-" + Guid.NewGuid().ToString("N")[..8], key.Name.Trim(), plaintext[4..12],
                key.Engine, "active", key.ExpiresAt, key.MaxUses, 0, null, DemoUsername, DateTimeOffset.Now);
            _keys.Add(summary);
            return new EnrollmentKeyCreated(summary, plaintext);
        });

    public Task<EnrollmentKeySummary> RevokeKeyAsync(string id, CancellationToken ct)
        => Guarded(() =>
        {
            var key = Find(_keys, k => k.Id == id, "No such enrollment key.");
            return key.RevokedAt is not null ? key : Replace(_keys, key, key with { Status = "revoked", RevokedAt = DateTimeOffset.Now });
        });

    public Task<IReadOnlyList<EnrollmentKeyEvent>> GetKeyEventsAsync(string id, int? limit, CancellationToken ct)
        => Guarded<IReadOnlyList<EnrollmentKeyEvent>>(() =>
        {
            Find(_keys, k => k.Id == id, "No such enrollment key.");
            return [.. _keyEvents.Where(e => e.Id.StartsWith(id + "/", StringComparison.Ordinal)).OrderByDescending(e => e.CreatedAt).Take(limit ?? 50)];
        });

    public Task<AdminPage<AdminAccount>> GetAdminsAsync(int offset, int limit, CancellationToken ct)
        => Guarded(() => Page(_admins.OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase), offset, limit));

    public Task<AdminAccount> CreateAdminAsync(AdminAccountCreate account, CancellationToken ct)
        => Guarded(() =>
        {
            var name = account.Username.Trim();
            if (name.Length == 0 || account.Password.Length < 12)
            {
                throw new PortalApiException("An administrator needs a user name and a password of at least 12 characters.", HttpStatusCode.BadRequest);
            }

            if (_admins.Any(a => string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PortalApiException($"An administrator named '{name}' already exists.", HttpStatusCode.BadRequest);
            }

            var created = new AdminAccount("adm-" + Guid.NewGuid().ToString("N")[..8], name, false, "local", DateTimeOffset.Now, null);
            _admins.Add(created);
            return created;
        });

    public Task<AdminAccount> DisableAdminAsync(string id, CancellationToken ct)
        => Guarded(() =>
        {
            var account = Find(_admins, a => a.Id == id, "No such administrator.");
            if (account.Username == DemoUsername)
            {
                throw new PortalApiException("You cannot disable the account you are signed in with.", HttpStatusCode.Conflict);
            }

            return Replace(_admins, account, account with { Disabled = true });
        });

    public Task ResetAdminPasswordAsync(string id, string password, CancellationToken ct)
        => Guarded(() =>
        {
            Find(_admins, a => a.Id == id, "No such administrator.");
            if (password.Length < 12)
            {
                throw new PortalApiException("A password must be at least 12 characters.", HttpStatusCode.BadRequest);
            }

            return 0;
        });

    public Task<AdminSettings> GetSettingsAsync(CancellationToken ct) => Guarded(() => _settings);

    public Task<AdminSettings> UpdateSettingsAsync(AdminSettings settings, CancellationToken ct)
        => Guarded(() =>
        {
            var chosen = settings.DefaultEngine?.Trim().ToLowerInvariant();
            if (chosen is not (EngineLabel.Action1 or EngineLabel.Agent))
            {
                throw new PortalApiException("Choose either Action1 or Agent.", HttpStatusCode.BadRequest);
            }

            _settings = new AdminSettings(chosen);
            return _settings;
        });

    /// <summary>
    /// Every call but the sign-in goes through here, so the demo refuses a missing token the way the
    /// server does: the session-ended path can be seen and tested without one.
    /// </summary>
    private Task<T> Guarded<T>(Func<T> read)
    {
        if (Token != DemoToken)
        {
            Unauthorized?.Invoke(this, EventArgs.Empty);
            return Task.FromException<T>(new PortalApiException(AdminApiClient.SessionEndedMessage, HttpStatusCode.Unauthorized));
        }

        lock (_gate)
        {
            try
            {
                return Task.FromResult(read());
            }
            catch (PortalApiException ex)
            {
                return Task.FromException<T>(ex);
            }
        }
    }

    private static AdminPage<T> Page<T>(IEnumerable<T> rows, int offset, int limit)
    {
        var all = rows.ToList();
        var take = Math.Clamp(limit, 1, AdminApiLimits.MaxLimit);
        var skip = Math.Max(0, offset);
        return new AdminPage<T>([.. all.Skip(skip).Take(take)], skip, take, skip + take < all.Count, all.Count);
    }

    private static T Find<T>(List<T> rows, Func<T, bool> match, string missing)
        => rows.FirstOrDefault(match) ?? throw new PortalApiException(missing, HttpStatusCode.NotFound);

    private static T Replace<T>(List<T> rows, T old, T replacement)
    {
        rows[rows.IndexOf(old)] = replacement;
        return replacement;
    }

    private AdminCatalogApp FindApp(string id)
        => Find(_catalog, a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase), $"No app with id '{id}'.");

    private AdminRequest Decide(string id, AppRequestStatus status, string? reason)
    {
        var request = Find(_requests, r => r.Id == id, "No such request.");
        if (request.Status != AppRequestStatus.Pending)
        {
            throw new PortalApiException("That request had already been decided.", HttpStatusCode.Conflict);
        }

        return Replace(_requests, request, request with
        {
            Status = status,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            DecidedBy = DemoUsername,
            DecidedAt = DateTimeOffset.Now,
        });
    }

    /// <summary>
    /// The catalog file format, close enough to the server's that an export from here imports there:
    /// camel case, indented, and nothing written for a field that is not set.
    /// </summary>
    private static readonly JsonSerializerOptions CatalogJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record DemoCatalogFile(List<AdminCatalogApp>? Apps);

    /// <summary>The rules the server applies to an app on a save or an import, with its wording.</summary>
    private static void ValidateApp(AdminCatalogApp app)
    {
        if (string.Equals(app.Id?.Trim(), "new", StringComparison.OrdinalIgnoreCase))
        {
            throw new PortalApiException("'new' is reserved for the create form. Give the app another id.", HttpStatusCode.BadRequest);
        }

        if (string.IsNullOrWhiteSpace(app.Id))
        {
            throw new PortalApiException("An app needs an id.", HttpStatusCode.BadRequest);
        }

        if (string.IsNullOrWhiteSpace(app.Name))
        {
            throw new PortalApiException("An app needs a name.", HttpStatusCode.BadRequest);
        }

        if (string.IsNullOrWhiteSpace(app.Action1?.PackageId) && app.Agent is null)
        {
            throw new PortalApiException("An app needs an Action1 package id or an agent package.", HttpStatusCode.BadRequest);
        }

        try
        {
            app.Agent?.Validate();
        }
        catch (System.IO.InvalidDataException ex)
        {
            throw new PortalApiException(ex.Message, HttpStatusCode.BadRequest);
        }
    }

    /// <summary>Replaces the app with the same id where it stands in the list, or adds it at the end.</summary>
    private void UpsertApp(AdminCatalogApp app)
    {
        var index = _catalog.FindIndex(a => string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _catalog[index] = app;
        }
        else
        {
            _catalog.Add(app);
        }
    }

    private static string DemoDeviceToken() => "apd_demo" + Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// What the device, enrollment key and admins pages need beyond the basic fleet: a key of every
    /// status, an attempt that failed, a device of each kind, the package managers agents report, and
    /// a directory account. Kept in this one method so the other pages' demo data stays apart from it.
    /// </summary>
    private void SeedFleetAdministration(DateTimeOffset now)
    {
        _keys.Add(new EnrollmentKeySummary("key-3", "Laptop refresh, spring", "Lp3sR8wz", "both", "expired",
            now.AddDays(-20), null, 7, null, DemoUsername, now.AddDays(-90)));
        _keys.Add(new EnrollmentKeySummary("key-4", "Pilot group", "Pg4tN2kc", EngineLabel.Agent, "exhausted",
            now.AddDays(60), 3, 3, null, "helpdesk", now.AddDays(-30)));

        // Every PC a key enrolled, then the attempts that made no new device. The id starts with the
        // key's, so one key's events are found without a field the contract does not have.
        foreach (var device in _devices.Where(d => d.EnrolledWithKeyId is not null))
        {
            _keyEvents.Add(new EnrollmentKeyEvent($"{device.EnrolledWithKeyId}/evt-{device.Id}", device.Id, device.Name, "setup",
                "enrolled", $"Enrolled {device.Name}.", device.CreatedAt));
        }

        _keyEvents.Add(new EnrollmentKeyEvent("key-1/evt-reenrol", "dev-3", "DESIGN-WS-02", "agent", "re-enrolled",
            "Re-enrolled DESIGN-WS-02 after a reinstall.", now.AddDays(-2)));
        _keyEvents.Add(new EnrollmentKeyEvent("key-2/evt-gone", "dev-gone", null, "setup", "enrolled",
            "Enrolled WAREHOUSE-TAB-2.", now.AddDays(-55)));
        _keyEvents.Add(new EnrollmentKeyEvent("key-2/evt-refused", null, null, "setup", "key-refused",
            "Refused: the key is revoked.", now.AddDays(-1)));

        // One device of each kind the table tells apart: Action1 and the agent, the agent alone, Action1
        // alone and disabled, and one that prefers an engine of its own.
        _devices[1] = _devices[1] with { EnginePreference = EngineLabel.Agent };
        _devices[2] = _devices[2] with { EndpointId = "" };
        _devices[3] = _devices[3] with { Enabled = false };

        _managers["dev-1"] = [new DeviceManager("winget", "1.9.25200")];
        _managers["dev-2"] = [new DeviceManager("winget", "1.9.25200"), new DeviceManager("choco", "2.4.1")];
        _managers["dev-3"] = [new DeviceManager("winget", "1.8.1911"), new DeviceManager("scoop", "", @"CONTOSO\mjones")];
        _managers["dev-5"] = [new DeviceManager("winget", "1.9.25200")];

        _admins.Add(new AdminAccount("adm-4", @"CONTOSO\jsmith", false, "directory", now.AddDays(-12), now.AddHours(-20)));
    }

    private void AddDevice(string id, string name, bool hasAgent, string? agentVersion, DateTimeOffset lastSeen, DateTimeOffset created)
    {
        var key = hasAgent ? _keys[0] : _keys[1];
        _devices.Add(new AdminDevice(id, name, "ep-" + id, true, hasAgent, null, agentVersion, key.Id, key.Name,
            null, created, lastSeen, 0));
    }

    private void AddApp(string id, string name, string publisher, string category, bool featured = false, bool hidden = false)
        => _catalog.Add(new AdminCatalogApp(id, name, publisher, $"{name}, installed the same way on every device.", category,
            Featured: featured, Hidden: hidden, Action1: new AdminAction1Package(id)));

    private void AddInstall(string deviceId, string appId, InstallState state, DateTimeOffset requestedAt, int percent, string detail,
        string requestedBy = @"CONTOSO\alee", string kind = InstallKind.Install)
    {
        var device = _devices.First(d => d.Id == deviceId);
        var app = _catalog.First(a => a.Id == appId);
        var finished = state is InstallState.Succeeded or InstallState.Failed or InstallState.Cancelled
            ? requestedAt.AddMinutes(2)
            : (DateTimeOffset?)null;
        var engine = device.HasAgent ? EngineLabel.Agent : EngineLabel.Action1;
        var number = _installs.Count + 1;
        // Only Action1 runs an install under an automation of its own, so only its rows carry a reference.
        _installs.Add(new AdminInstall("ins-" + number, app.Id, app.Name, device.Id, device.Name, device.EndpointId,
            requestedBy, engine, kind, state, percent, detail,
            null, null, 0, 0, requestedAt, finished, finished ?? DateTimeOffset.Now,
            engine == EngineLabel.Action1 ? $"auto-{number:D4}" : null));
        var index = _devices.IndexOf(device);
        _devices[index] = device with { InstallCount = device.InstallCount + 1 };
    }

    /// <summary>
    /// History for the installs and requests pages, beyond what the dashboard needs: more than one
    /// requester and every state, a removal, an Action1 install that cannot be stopped from here, and
    /// more than one pending request to work through.
    /// </summary>
    private void AddInstallAndRequestHistory(DateTimeOffset now)
    {
        AddInstall("dev-3", "vscode", InstallState.Running, now.AddMinutes(-8), 70, "Running the installer.", @"CONTOSO\mjones");
        AddInstall("dev-4", "google-chrome", InstallState.Running, now.AddMinutes(-12), 30, "Action1 is deploying the package.", @"CONTOSO\tbrown");
        AddInstall("dev-2", "obs", InstallState.Cancelled, now.AddHours(-3), 20, "Stopped by helpdesk.", @"CONTOSO\pkaur");
        AddInstall("dev-5", "obs", InstallState.Succeeded, now.AddHours(-30), 100, "The packages have been removed successfully.",
            @"CONTOSO\alee", InstallKind.Uninstall);
        AddInstall("dev-3", "mozilla-firefox", InstallState.Succeeded, now.AddDays(-3), 100, "The packages have been installed successfully.", @"CONTOSO\mjones");
        AddInstall("dev-1", "7-zip", InstallState.Failed, now.AddDays(-6), 0, "The download failed: the connection was reset.", @"CONTOSO\tbrown");
        AddInstall("dev-4", "7-zip", InstallState.Succeeded, now.AddDays(-12), 100, "The packages have been installed successfully.", @"CONTOSO\tbrown");
        AddInstall("dev-2", "google-chrome", InstallState.Succeeded, now.AddDays(-13), 100, "The packages have been installed successfully.", @"CONTOSO\pkaur");

        _requests.Add(new AdminRequest("req-5", "Zoom Workplace, for customer calls", "WAREHOUSE-TAB-1", @"CONTOSO\tbrown",
            AppRequestStatus.Pending, null, null, now.AddMinutes(-40), null));
        _requests.Add(new AdminRequest("req-6", "Power BI Desktop, for the monthly figures", "FINANCE-LT-04", null,
            AppRequestStatus.Approved, null, "helpdesk", now.AddDays(-6), now.AddDays(-6).AddHours(2)));
        _requests.Add(new AdminRequest("req-7", "A game for the break room PC", "RECEPTION-01", @"CONTOSO\alee",
            AppRequestStatus.Denied, "Work devices only run work software.", DemoUsername, now.AddDays(-15), now.AddDays(-15).AddMinutes(20)));
    }

    /// <summary>
    /// The catalog editor's demo data: at least one app from every kind of source the Source selector
    /// offers, and one with both an Action1 and an agent package so the engine override has something
    /// to decide. Kept apart from the fleet above so other pages' demo data does not have to move.
    /// </summary>
    private void AddCatalogSources()
    {
        void With(string id, Func<AdminCatalogApp, AdminCatalogApp> change)
        {
            var app = _catalog.First(a => a.Id == id);
            Replace(_catalog, app, change(app));
        }

        With("vscode", app => app with
        {
            EngineOverride = EngineLabel.Agent,
            Requires = ["git"],
            Agent = new WingetPackageDefinition("Microsoft.VisualStudioCode", "machine"),
        });
        With("7-zip", app => app with
        {
            Action1 = new AdminAction1Package(""),
            Agent = new ManagedPackageDefinition("choco", "7zip", "machine"),
        });
        With("obs", app => app with
        {
            Action1 = new AdminAction1Package(""),
            Agent = new WingetPackageDefinition("OBSProject.OBSStudio", "machine", RequiresReboot: true),
        });

        _catalog.Add(new AdminCatalogApp("git", "Git", "The Git Development Community", "Distributed version control.", "Developer tools",
            Action1: new AdminAction1Package(""), Agent: new WingetPackageDefinition("Git.Git", "machine")));
        _catalog.Add(new AdminCatalogApp("whatsapp", "WhatsApp", "WhatsApp Inc.", "Messaging, from the Microsoft Store.", "Communication",
            Requirements: "Needs the person signed in to the Microsoft Store.",
            Action1: new AdminAction1Package(""), Agent: new WingetPackageDefinition("9NKSQGP7F2NH", "user", Source: WingetSources.Store)));
        _catalog.Add(new AdminCatalogApp("ripgrep", "ripgrep", "BurntSushi", "Searches files for a pattern, fast.", "Developer tools",
            UserRemovable: true, Match: new AdminMatchRule(null, "ripgrep"),
            Action1: new AdminAction1Package(""), Agent: new ManagedPackageDefinition("scoop", "main/ripgrep", "user")));
        _catalog.Add(new AdminCatalogApp("steam", "Steam", "Valve Corporation", "The Steam game launcher.", "Games",
            Requirements: "Needs a Steam account.",
            Action1: new AdminAction1Package(""),
            Agent: new DirectPackageDefinition("https://cdn.vendor.example/SteamSetup.exe",
                "3f1c7d0e9b8a6f5e4d3c2b1a0f9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f10", "exe", "/S", 3_145_728, "Steam", "machine")));
    }
}
