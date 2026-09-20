# App Portal

A self-service software catalog for Windows PCs managed with [Action1](https://www.action1.com/). The person at the keyboard opens App Portal, picks an approved app, and the Action1 agent installs it as SYSTEM. No administrator rights on the PC, no installer download, no API credential on the device.

![Apps view in light theme](docs/images/apps-light.png)

The client follows the Windows 11 design language: a two-layer NavigationView layout with Mica behind the pane, the Fluent 2 color tokens for light and dark, Segoe UI Variable on the Windows type ramp, 4px control and 8px container corner radii, and a 3x16 accent selection indicator. It picks up the system theme automatically.

| Dark theme | Activity |
|---|---|
| ![Apps view in dark theme](docs/images/apps-dark.png) | ![Activity view](docs/images/activity.png) |

## How it works

```
+-------------------+   device token    +--------------------+   API credential   +-----------------+
| App Portal client | ----------------> | App Portal server  | -----------------> | Action1 cloud   |
| (Windows, Avalonia)|  HTTPS, JSON     | (ASP.NET Core,     |  OAuth2 + REST 3.0 |                 |
+-------------------+                   |  Docker)           |                    +--------+--------+
                                        +--------------------+                             |
                                                                                            v
                                                                                   Action1 agent on the PC
                                                                                   installs the package
```

1. The client reads `%ProgramData%\AppPortal\client.json` for the server URL and this device's token, then shows the catalog.
2. Install sends `POST /api/v1/installs {appId}`. The server checks the token, maps the device to its Action1 endpoint ID, resolves the package version in the Software Repository, and runs a `deploy_package` automation on that one endpoint.
3. The server polls the automation's endpoint result and records Queued, Running, Succeeded, Failed or Cancelled. The client shows progress on the card and the full history under Activity.
4. Installed shows what Action1's software inventory reports for the device, matched back to catalog entries.
5. Requests lets the user ask for software outside the catalog. An administrator approves or denies the request in the browser; the client shows the decision and reason on its next refresh.

The server stores the catalog, devices, install history, requests and admin accounts in one SQLite database on the data volume. The client sends `X-AppPortal-User: DOMAIN\user` with API calls so installs and requests record who asked. This account name is a client-supplied label; the device token authenticates the call.

The Action1 API credential lives only on the server, supplied through its environment at start. A device token grants access to the catalog, installs and requests for that device; it cannot administer the portal or act on another endpoint.

## Why this exists

Action1 announced a Self-Service App Portal in October 2025 and lists it as an upcoming release on its roadmap. Until it ships, this is the gap-filler for a locked-down workstation where AppLocker allows only what lands in Program Files through the management agent. The client itself installs to Program Files for that reason.

## Try it without a server

Download `AppPortal-client-win-x64.zip` from the [latest release](https://github.com/Duresa7/app-portal/releases/latest), unzip it, and run:

```
AppPortal.exe --demo
```

Demo mode fills the whole interface with sample data held in memory. Installs advance through queued, installing and installed over about twelve seconds, then appear under Installed. Nothing is installed on the machine and nothing leaves it. The build is self-contained, so no .NET runtime is needed, and it is unsigned, so Windows SmartScreen will ask before running it the first time.

## Repository layout

| Path | What |
|---|---|
| `src/AppPortal.Shared` | API contracts shared by client and server |
| `src/AppPortal.Server` | ASP.NET Core minimal API, Action1 client, SQLite storage and migrations, CLI |
| `src/AppPortal.Client` | Avalonia desktop client (Windows target; runs on Linux for development) |
| `src/AppPortal.Updater` | Self-contained updater run by a SYSTEM scheduled task; replaces the client from GitHub releases |
| `tests/AppPortal.Server.Tests` | xUnit tests against an in-memory Action1 stand-in |
| `tests/AppPortal.Client.Tests` | Client catalog refresh regression tests |
| `tests/AppPortal.Updater.Tests` | xUnit tests for version parsing, checksum parsing and the file swap |
| `deploy/` | Dockerfile, compose file, environment template, server smoke test, Windows install script |
| `docs/` | Screenshots and design notes |

## Server setup

Requirements: Docker and an Action1 API credential. A secrets manager whose CLI can render an environment file from references is the comfortable way to keep the credential, but any way of writing three lines into a mode-600 file works.

1. **Create the API credential** in the Action1 console under Configuration, API Credentials. Store the Client ID and Client Secret in your secrets manager together with your organization ID (the `org=` value in the console URL). The server needs `view_endpoints`, `view_software_repository`, `view_installed_software`, `view_automations` and `run_automations`.
2. **Write the environment file.** Copy `deploy/server.env.example` to `deploy/server.env`, which is gitignored, and fill in the three `Action1__` values, either by hand or by rendering the file from your secrets manager's references. Keep it mode 600; it is the only place the credential exists on the host.
3. **Start the server**:
   ```bash
   docker compose -f deploy/compose.yaml up -d
   ```
   This pulls `ghcr.io/duresa7/app-portal-server`. Put `APP_PORTAL_VERSION=0.3.0` in `deploy/.env` to pin the release, or add `--build` to build from the checkout. The checked-in catalog seeds an empty database; its package IDs are examples and must be verified against your Action1 Software Repository.
4. **Create the first administrator**:
   ```bash
   docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll admin add --username admin
   ```
   Enter the password at the prompt. For unattended setup, supply `APPPORTAL_ADMIN_PASSWORD` through the process environment. Open `/admin` on the server, sign in, and manage further accounts under **Admins**.
5. **Prepare the catalog.** Open **Catalog** to add or edit apps, search and verify Action1 packages, or import a JSON catalog. Hide removes an app from the device catalog while retaining its history; delete is refused when installs reference it. Export downloads the current catalog. See [the catalog format](deploy/config/README.md).
6. **Register a device.** Open **Devices**, enter its name and Action1 endpoint ID, and copy the device token shown once. Store it in your secrets manager and pass it to the client installer. Only the token's SHA-256 is stored on the server.

The CLI remains available for scripts:

```bash
docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll catalog import /app/config/catalog.json
docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll catalog export
docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll catalog verify
docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll device add --name OBIPC --endpoint-id <endpoint-id>
```

Put the server behind TLS (a reverse proxy or your tunnel) before a device on another network uses it. The token is a bearer secret.

## Administration

Sign in at `/admin` with a local administrator account. Browser sessions use cookies and forms require antiforgery tokens. Admin API sessions use separate `apa_` bearer tokens; device tokens cannot open admin pages.

**Installs** shows fleet-wide history with device, requester, app, engine, state and dates. Filter by device, app, state, requester or date range; the table refreshes every 30 seconds.

![Install history in the admin UI](docs/images/admin-installs.png)

**Catalog** manages approved software. Edits appear in the client on its next refresh, including changes to existing cards.

![Catalog management in the admin UI](docs/images/admin-catalog.png)

**Requests** shows pending and decided software requests. Approve or deny with an optional reason of up to 500 characters. Approval records a decision; it does not add an app or trigger an installation.

![Software requests in the admin UI](docs/images/admin-requests.png)

**Devices** supports renaming, disabling, token rotation and removal. Disabling or rotating a token takes effect on the next API call. Removal is refused while an install is active; afterward, install and request history remains available to administrators. A replacement device does not inherit the retired device's history.

**Enrollment keys** lets administrators create and revoke keys with an expiry, use limit and default engine. The full key appears once. Automatic enrollment and the local agent arrive in milestone 2; version 0.3.0 still uses manual device registration and Action1 for installs.

### Directory sign-in (optional)

The portal needs no directory. A deployment that already runs Active Directory can let administrators sign in with their domain account instead of a second password, by configuring the `Directory` section:

```jsonc
"Directory": {
  "Enabled": true,
  "Servers": ["dc01.ad.example.com", "dc02.ad.example.com"],
  "Port": 636,
  "NetBiosDomain": "EXAMPLE",
  "RequiredGroup": "APP-AppPortal-Admins",
  "CertificateThumbprints": ["<sha-256 of the controller certificate>"],
  "TimeoutSeconds": 10
}
```

How it behaves:

- **Local accounts are checked first**, so a directory that is unreachable cannot lock you out of your own portal. Keep one local account.
- The bind is **LDAPS only** and uses the signing-in user's own credentials; the server holds no service account.
- Only members of `RequiredGroup` are admitted, nested groups included. There is no default group: leaving it empty stops the server rather than admitting the whole directory.
- A forest with no certificate authority gives its controllers self-signed certificates. List their SHA-256 thumbprints in `CertificateThumbprints` to pin them; with no thumbprints, ordinary chain validation applies and a self-signed certificate is refused.
- The first successful sign-in creates an administrator row named `DOMAIN\user`, matching the requester label on installs. Disable it like any other account; its password stays in the directory and cannot be set here.
- Every administrator is a full administrator. There is no group-to-role mapping, no directory sync, and the Windows client does not use this.

Sign in with `DOMAIN\user`, a UPN, or the bare user name when `NetBiosDomain` is set. Configure the section through the environment like any other setting, for example `Directory__Enabled=true` and `Directory__Servers__0=dc01.ad.example.com`. The controllers must be resolvable and reachable on 636 from the container; `extra_hosts` in Compose covers a name your Docker host cannot resolve.

## Upgrading from 0.2.x

Stop the old container and back up both the data volume and `deploy/config` before starting 0.3.0. Keep the existing volume mounted at `/app/data` and the catalog available at `/app/config/catalog.json` for the first start. The server imports `devices.json`, `installs.json` and the catalog into `app-portal.db`; existing device tokens remain valid. Verify the device list, catalog and install history, then create the first administrator with `admin add`.

After verification, archive the legacy JSON files outside the mounted directories. The config volume no longer needs `catalog.json`: edits now live in SQLite, and explicit CLI or browser imports remain available. Leaving legacy files in place can seed a table again if it becomes empty. Back up the data volume while the server is stopped, including the database and admin authentication keys. To roll back, restore the pre-upgrade backup and old image; 0.2.x does not read SQLite.

The upgrade was checked using a copy of a data volume written by the 0.2.1 server in fake mode. Its original token authenticated after migration, catalog and install records remained available, and a restart produced no duplicate records.

## Client deployment

The CI workflow publishes `AppPortal-client-win-x64.zip`: a self-contained build plus `Install-AppPortalClient.ps1`. Deploy it through Action1 as a custom package (or run it as SYSTEM any other way):

```powershell
.\Install-AppPortalClient.ps1 -ServerUrl https://portal.example.internal -DeviceToken <token>
```

The script copies the client to `%ProgramFiles%\App Portal`, writes `%ProgramData%\AppPortal\client.json` readable by Users and writable only by Administrators, adds a Start menu shortcut for all users, registers an uninstall entry, and registers the updater task described next. Pass the token through the RMM's secret parameter rather than embedding it in the package.

The client's **Requests** section accepts up to 500 characters describing the software needed. Each device can have 20 pending requests. The newest request appears immediately after submission; status and administrator reasons refresh with the rest of the client.

![Requests in the Windows client](docs/images/requests.png)

## Updates

Deploy the client once. After that it keeps itself current from this repository's releases.

`AppPortal.Updater.exe` sits beside the client and runs from a scheduled task, **App Portal Updater**, as SYSTEM: five minutes after boot, a minute after any logon, once a day at a random time between noon and one, and whenever a user presses the update button in the client. Each run:

1. Asks `api.github.com` for the latest release and compares its tag with the installed `AppPortal.exe` version.
2. Downloads `AppPortal-client-win-x64.zip` while hashing it, fetches `SHA256SUMS` from the same release, and discards the archive on any mismatch. A release without checksums is refused.
3. Unpacks the archive's `client` folder into `%ProgramFiles%\App Portal\.staged`.
4. If no client from that folder is running, moves the current files into `.previous`, moves the staged files into place, and updates the uninstall entry's version. Windows lets a running executable be renamed but not overwritten, which is why the swap is two moves and why the updater can replace itself. If anything fails half-way, the old files move back.
5. Writes `%ProgramData%\AppPortal\update.json` and appends to `updater.log` in the same folder.

The client never touches the release feed. It reads `update.json` and shows one of two banners: *available*, with an **Update now** button that starts the task; or *ready*, once a build is staged, with **Restart to update**, which starts the task and exits so the swap can proceed. The task's security descriptor grants Authenticated Users read and execute, so a standard user can start it and nothing else; only SYSTEM and Administrators can write to Program Files, so nothing a user controls can put a build on the machine. A client that is left open is never killed: the swap waits for the next run.

What the checksum does and does not prove: it catches a truncated or corrupted download and a mismatch between the archive and what CI published. It does not defend against a compromised GitHub account, because the checksums come from the same release. The releases are unsigned; that is the next thing to add. To point installations at a fork, add `"updateRepository": "owner/name"` to `client.json`. Run `AppPortal.Updater.exe --check` from an elevated prompt to look without changing anything.

## Development

```bash
dotnet test
cd src/AppPortal.Server
ASPNETCORE_ENVIRONMENT=Development dotnet run -- device add --name DEVPC --endpoint-id fake-endpoint-0001
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5080 dotnet run
```

The Development environment uses `Action1:Mode=Fake`: an in-memory Action1 whose deployments advance one step per status read, so the whole flow runs without a tenant. Then, in another shell:

```bash
cd src/AppPortal.Client
APPPORTAL_SERVER_URL=http://127.0.0.1:5080 APPPORTAL_DEVICE_TOKEN=<token> dotnet run
```

The version every project carries is in `Directory.Build.props`; a release build gets the tag's version from CI through `-p:Version=`, and the updater compares that with the installed file version, so tag `v0.3.0` must ship binaries that report 0.3.0.

`dotnet run -- --screenshot out.png 2 --theme dark` renders a section (0 apps, 1 installed, 2 activity, 3 requests) in the chosen theme to a PNG and exits, which is how the images in `docs/` were produced under Xvfb.

Before pushing, `dotnet format` puts the code in the shape CI checks for, and `deploy/smoke-test.sh <image>` runs the same server smoke test CI runs against a locally built image.

## Releasing

Every push runs the format check, build and tests on Linux and Windows, publishes the client zip and verifies it on Windows (checksum, file list, binaries report the props version, the client starts in demo mode and renders), and builds the server image and exercises it in fake mode. A release is cut by tagging:

```bash
# Directory.Build.props already says 0.3.0 and that commit is on main
git tag v0.3.0 && git push origin v0.3.0
```

The tag run repeats all of the above, then a final job pushes `ghcr.io/duresa7/app-portal-server:0.3.0` and `:latest` and creates the GitHub release with the zip and `SHA256SUMS`. Nothing a device or a server host can pull exists before that job, so a failure anywhere leaves no release. The run refuses a tag whose version differs from `Directory.Build.props` or whose commit is not on main. A repository ruleset lets only administrators create, move or delete `v*` tags.

Two things CI cannot do:

- **Package visibility.** The first push creates the GHCR package private. Open the package's settings once, under Package settings, Danger Zone, and change visibility to public so a server host can pull without a token.
- **Yanking a bad release.** Clients only move forward and discard the previous build, so a release that reaches devices cannot be recalled. Delete the release and its tag so no further device picks it up, fix, bump the version and tag again. Devices that already updated get the fix on their next check.

## API

Device routes below need `Authorization: Bearer <device token>`. `/healthz` is public; admin session routes use the credentials described separately.

| Route | Purpose |
|---|---|
| `GET /api/v1/catalog` | Approved apps, without package identifiers |
| `GET /api/v1/device` | The calling device and its Action1 endpoint status |
| `GET /api/v1/device/installed` | Installed software reported by Action1, matched to catalog IDs |
| `GET /api/v1/installs` | This device's install history, active ones refreshed |
| `GET /api/v1/installs/{id}` | One install request, refreshed |
| `POST /api/v1/installs` | `{ "appId": "..." }`, answers 202 with the record; 404 unknown app, 409 already in progress, 422 no matching package version, 429 too many active, 502 Action1 refused |
| `GET /api/v1/requests` | This device's software requests, newest first |
| `POST /api/v1/requests` | `{ "text": "..." }`; 201 created, 400 empty or over 500 characters, 429 at the pending limit |

| Admin session route | Purpose |
|---|---|
| `POST /api/v1/admin/session` | `{ "username": "...", "password": "..." }`; issue an `apa_` admin bearer token |
| `DELETE /api/v1/admin/session` | Revoke the calling admin bearer token; 204 on success |

The admin session API is available in 0.3.0; the full admin JSON API is planned for milestone 4. Browser administration lives under `/admin`.

## Reliability notes

A few behaviours are deliberate and were put in after a review found the failure they prevent:

- **One install request per device at a time wins.** `InstallService.CreateAsync` takes a per-device gate, so two overlapping requests cannot both pass the duplicate and concurrency checks and start two Action1 deployments.
- **A finished install stays finished.** The background poller and every client refresh update the same record from their own snapshots. `InstallStore.Upsert` drops a write that carries an older `LastCheckedAt` than what is stored, and never moves a terminal state back to active.
- **State is one database, written in transactions.** Catalog, devices and install history live in `app-portal.db` on the data volume. A refresh that would overwrite a newer one is dropped inside the same transaction that read it, so two callers cannot interleave. A file that cannot be opened stops the server at start rather than leaving it serving an empty catalog to every device.
- **The client does not die on a bad response.** An empty body or an unexpected content type, which a reverse proxy can produce, surfaces as a message in the window rather than an unhandled exception from a timer callback. Anything that still escapes is appended to `%LocalAppData%\AppPortal\client.log`.

## Limits

- One catalog for all devices. Per-device or per-group catalogs are not implemented.
- Uninstall is not offered to the user; Action1 supports it and the server could expose it later.
- Device tokens do not expire. Rotate them on the device detail page, or run `device add` again for the same name.
- Admin sign-in uses local accounts. OpenID Connect and email notifications are not implemented.
- Request approval is a recorded decision; an administrator must separately add any approved software to the catalog.
- Enrollment keys can be managed, but automatic enrollment, the Windows agent and non-Action1 install engines are not yet implemented.
- Updates come only from GitHub releases over HTTPS, verified by SHA-256 but not signed. A machine without internet access keeps the build it has.
- Action1's API is rate limited (HTTP 429). The server polls active installs every 30 seconds by default; keep the catalog small and the device count modest.

## License

MIT. See [LICENSE](LICENSE). Action1 is a trademark of Action1 Corporation; this project is not affiliated with or endorsed by Action1.
