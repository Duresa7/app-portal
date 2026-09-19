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

The Action1 API credential lives only on the server, supplied through its environment at start. A device token can request installs of catalog apps on its own endpoint and nothing else.

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
| `src/AppPortal.Server` | ASP.NET Core minimal API, Action1 client, catalog and device stores, CLI |
| `src/AppPortal.Client` | Avalonia desktop client (Windows target; runs on Linux for development) |
| `src/AppPortal.Updater` | Self-contained updater run by a SYSTEM scheduled task; replaces the client from GitHub releases |
| `tests/AppPortal.Server.Tests` | xUnit tests against an in-memory Action1 stand-in |
| `tests/AppPortal.Updater.Tests` | xUnit tests for version parsing, checksum parsing and the file swap |
| `deploy/` | Dockerfile, compose file, environment template, Windows install script |
| `docs/` | Screenshots and design notes |

## Server setup

Requirements: Docker and an Action1 API credential. A secrets manager whose CLI can render an environment file from references is the comfortable way to keep the credential, but any way of writing three lines into a mode-600 file works.

1. **Create the API credential** in the Action1 console under Configuration, API Credentials. Store the Client ID and Client Secret in your secrets manager together with your organization ID (the `org=` value in the console URL). The server needs `view_endpoints`, `view_software_repository`, `view_installed_software`, `view_automations` and `run_automations`.
2. **Write the environment file.** Copy `deploy/server.env.example` to `deploy/server.env`, which is gitignored, and fill in the three `Action1__` values, either by hand or by rendering the file from your secrets manager's references. Keep it mode 600; it is the only place the credential exists on the host.

The script copies the client to `%ProgramFiles%\App Portal`, writes `%ProgramData%\AppPortal\client.json` readable by Users and writable only by Administrators, adds a Start menu shortcut for all users, registers an uninstall entry, and registers the updater task described next. Pass the token through the RMM's secret parameter rather than embedding it in the package.

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

`dotnet run -- --screenshot out.png 2 --theme dark` renders a section (0 apps, 1 installed, 2 activity) in the chosen theme to a PNG and exits, which is how the images in `docs/` were produced under Xvfb.

## API

All routes except `/healthz` need `Authorization: Bearer <device token>`.

| Route | Purpose |
|---|---|
| `GET /api/v1/catalog` | Approved apps, without package identifiers |
| `GET /api/v1/device` | The calling device and its Action1 endpoint status |
| `GET /api/v1/device/installed` | Installed software reported by Action1, matched to catalog IDs |
| `GET /api/v1/installs` | This device's install history, active ones refreshed |
| `GET /api/v1/installs/{id}` | One install request, refreshed |
| `POST /api/v1/installs` | `{ "appId": "..." }`, answers 202 with the record; 404 unknown app, 409 already in progress, 422 no matching package version, 429 too many active, 502 Action1 refused |

## Reliability notes

A few behaviours are deliberate and were put in after a review found the failure they prevent:

- **One install request per device at a time wins.** `InstallService.CreateAsync` takes a per-device gate, so two overlapping requests cannot both pass the duplicate and concurrency checks and start two Action1 deployments.
- **A finished install stays finished.** The background poller and every client refresh update the same record from their own snapshots. `InstallStore.Upsert` drops a write that carries an older `LastCheckedAt` than what is stored, and never moves a terminal state back to active.
- **A damaged device file does not take the API down.** `devices.json` is written to a temporary file and renamed, and a file that fails to parse is logged while the last good device list keeps serving.
- **The client does not die on a bad response.** An empty body or an unexpected content type, which a reverse proxy can produce, surfaces as a message in the window rather than an unhandled exception from a timer callback. Anything that still escapes is appended to `%LocalAppData%\AppPortal\client.log`.

## Limits

- One catalog for all devices. Per-device or per-group catalogs are not implemented.
- Uninstall is not offered to the user; Action1 supports it and the server could expose it later.
- Device tokens do not expire. Rotate one by running `device add` again for the same name.
- Updates come only from GitHub releases over HTTPS, verified by SHA-256 but not signed. A machine without internet access keeps the build it has.
- Action1's API is rate limited (HTTP 429). The server polls active installs every 30 seconds by default; keep the catalog small and the device count modest.

## License

MIT. See [LICENSE](LICENSE). Action1 is a trademark of Action1 Corporation; this project is not affiliated with or endorsed by Action1.
