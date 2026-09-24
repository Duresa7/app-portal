# Development

## Repository layout

| Path | What |
|---|---|
| `src/AppPortal.Shared` | API contracts shared by client and server |
| `src/AppPortal.Server` | ASP.NET Core minimal API, Action1 client, SQLite storage and migrations, CLI |
| `src/AppPortal.Client` | Avalonia desktop client (Windows target; runs on Linux for development) |
| `src/AppPortal.Agent` | SYSTEM service for enrollment, heartbeats, installs and self-update from GitHub releases |
| `src/AppPortal.Installer` | WiX v5 MSI, built and verified on Windows |
| `src/AppPortal.Setup` | `AppPortalSetup.exe`: the wizard and silent installer that carries the MSI |
| `tests/AppPortal.Server.Tests` | xUnit tests against an in-memory Action1 stand-in |
| `tests/AppPortal.Client.Tests` | Client catalog refresh regression tests |
| `tests/AppPortal.Agent.Tests` | xUnit tests for enrollment, heartbeats, the install engines and self-update |
| `tests/AppPortal.Setup.Tests` | xUnit tests for the wizard's arguments, exit codes and enrollment wait |
| `deploy/` | Dockerfile, compose file, environment template, server smoke test and the Windows installer test |
| `docs/` | Guides, screenshots, design notes and plans |

## Run it locally

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

For the client alone, `dotnet run --project src/AppPortal.Client -- --demo` fills the whole interface with sample data held in memory. Installs advance through queued, installing and installed over about twelve seconds, then appear under Installed. Nothing is installed on the machine and nothing leaves it. An installed client does the same with `"%ProgramFiles%\App Portal\AppPortal.exe" --demo`, which is how the release gate proves the packaged build renders on Windows.

The version every project carries is in `Directory.Build.props`; a release build gets the tag's version from CI through `-p:Version=`, and the agent compares that with the installed file version, so tag `v0.6.0` must ship binaries that report 0.6.0.

`dotnet run -- --screenshot out.png 2 --theme dark` renders a section (0 apps, 1 installed, 2 activity, 3 requests) in the chosen theme to a PNG and exits, which is how the images in `docs/` were produced under Xvfb.

The Windows-only installer project is intentionally outside `AppPortal.sln`, so the solution builds and tests on Linux. On Windows, publish `src/AppPortal.Client` and `src/AppPortal.Agent` with `-c Release -r win-x64 --self-contained` to `out/client` and `out/agent`, then run `dotnet build src/AppPortal.Installer/AppPortal.Installer.wixproj -c Release -o out/installer`. Its version comes from `Directory.Build.props`.

Before pushing, `dotnet format` puts the code in the shape CI checks for, and `deploy/smoke-test.sh <image>` runs the same server smoke test CI runs against a locally built image.

## Releasing

CI runs in two shapes, because Windows minutes bill at several times the Linux rate and the Windows jobs are most of the cost of the workflow.

- **Every push and pull request:** the format check, build and tests on Linux, and the server image built and exercised in fake mode.
- **Before a release:** the same plus everything on Windows. A `v*` tag runs it automatically; at any other time start it from the Actions tab with the *Run the Windows jobs as well* box ticked. Treat a red result there as blocking the tag.

The Windows half is what proves the thing a PC actually receives. `build-windows` publishes the client and the agent and builds the MSI and the bootstrapper, always on a GitHub-hosted runner. `installer-verify` then downloads exactly those files, wherever the `WINDOWS_RUNNER` repository variable sends it, and runs [`deploy/windows/ci-installer-test.ps1`](../deploy/windows/ci-installer-test.ps1) against a fake-mode server started in the job: the MSI must report the props version and the unchanging upgrade code, `AppPortalSetup.exe /quiet` must return 0, the service must come up as SYSTEM, the device must enroll and appear on `/admin/devices` with a heartbeat, the installed client must render, the uninstall must leave nothing behind, and an install of the previous release must upgrade in place without losing its device token. The script takes the same arguments by hand, so a failure that only reproduces on a virtual machine can be chased there:

```powershell
./deploy/windows/ci-installer-test.ps1 -Msi out/installer/AppPortal-0.5.0-x64.msi -Version 0.5.0 -Setup out/setup/AppPortalSetup.exe -Signing Off
```

It installs and uninstalls software and writes to `%ProgramData%`, so run it on a throwaway machine. `-Signing Test` or `-Signing Release` also fails the run on any App Portal file, packaged or installed, that lacks the signature that mode promises; `Off`, the default, only reports.

Signing through SignPath Foundation is switched by repository settings, never by a file. With the `SIGNPATH_ORGANIZATION_ID` variable and the `SIGNPATH_API_TOKEN` secret both set, a `v*` tag is release-signed and a run of `main` started from the Actions tab with *sign* ticked is test-signed, as a rehearsal. Every other run, including every pull request, fork and branch, builds unsigned; without the variable, so does everything. The variable set without the secret fails the run in its first job rather than shipping unsigned. `SIGNPATH_PROJECT_SLUG` overrides the project name `app-portal` if SignPath's differs.

On a tag with signing on, `build-windows` submits three signing requests in order, the payload, the MSI, then `AppPortalSetup.exe`, because each package carries the one before it. The owner approves each in SignPath as it arrives, within an hour of its submission. A run that timed out is re-run with *Re-run failed jobs*, which submits fresh requests. Once a signed release exists, a tag that would publish an unsigned one fails in `installer-verify`: every agent the signed release installed would refuse it.

A release is cut by tagging:

```bash
# Directory.Build.props already says 0.6.0 and that commit is on main
git tag v0.6.0 && git push origin v0.6.0
```

The tag run repeats all of the above, then a final job pushes `ghcr.io/duresa7/app-portal-server:0.6.0` and `:latest` and creates the GitHub release with the MSI, `AppPortalSetup.exe` and `SHA256SUMS`. Nothing a device or a server host can pull exists before that job, so a failure anywhere leaves no release. The run refuses a tag whose version differs from `Directory.Build.props` or whose commit is not on main. A repository ruleset lets only administrators create, move or delete `v*` tags.

Two things CI cannot do:

- **Package visibility.** The first push creates the GHCR package private. Open the package's settings once, under Package settings, Danger Zone, and change visibility to public so a server host can pull without a token.
- **Yanking a bad release.** Clients only move forward and discard the previous build, so a release that reaches devices cannot be recalled. Delete the release and its tag so no further device picks it up, fix, bump the version and tag again. Devices that already updated get the fix on their next check.

## API

[`api.md`](api.md) is the full HTTP reference: every route with its verb, authentication, body shapes and status codes.
