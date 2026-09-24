# How App Portal works

App Portal has three parts:

- **The server**, an ASP.NET Core app in Docker. It holds the catalog, the devices, the install history, the requests and the administrator accounts in one SQLite database.
- **The client**, an Avalonia app for Windows. It is what the person at the keyboard uses.
- **The agent**, a Windows service that runs as SYSTEM on each PC. It enrolls the PC, installs software, and keeps App Portal itself up to date.

```
+-------------------+   device token    +--------------------+   API credential   +-----------------+
| App Portal client | ----------------> | App Portal server  | -----------------> | Action1 cloud   |
| (Windows)         |   HTTPS, JSON     | (ASP.NET Core,     |  OAuth2 + REST 3.0 +--------+--------+
+-------------------+                   |  Docker)           |                             |
                                        +---------+----------+                             v
                                                  ^                               Action1 agent on the PC
                                                  | jobs, long-poll                installs the package
                                        +---------+----------+
                                        | App Portal agent   |
                                        | (SYSTEM service)   |
                                        +--------------------+
```

1. The client reads `%ProgramData%\AppPortal\client.json` for the server URL and this device's token, then shows the catalog.
2. Install sends `POST /api/v1/installs {appId}`. The server checks the token and picks the install engine for that app on that device. For Action1, it maps the device to its Action1 endpoint ID, resolves the package version in the Software Repository, and runs a `deploy_package` automation on that one endpoint. For the agent, it queues a job that the agent on the PC picks up.
3. The server records Queued, Running, Succeeded, Failed or Cancelled. The client shows progress on the card and the full history under Activity.
4. Installed shows what Action1's software inventory and the agent report for the device, matched back to catalog entries.
5. Requests lets the user ask for software outside the catalog. An administrator approves or denies the request; the client shows the decision and reason on its next refresh.

The client sends `X-AppPortal-User: DOMAIN\user` with API calls so installs and requests record who asked. This account name is a client-supplied label; the device token authenticates the call.

The Action1 API credential lives only on the server, supplied through its environment at start. A device token grants access to the catalog, installs and requests for that device; it cannot administer the portal or act on another endpoint.

## Two install engines

App Portal installs software one of two ways, and a device may have both.

- **action1**: the server asks an Action1 automation to deploy a package to that endpoint. This is what the portal started as, and a fleet already managed by Action1 needs nothing else.
- **agent**: the App Portal agent on the PC installs a winget package or downloads an installer the catalog names, checks its SHA-256, and runs it. A company with no RMM at all can run the portal on this alone.

A server-wide default decides between them when both could serve an app; a catalog app or a device can override it. Every install says which engine ran it, on the card and in the history.

What the agent adds beyond "run an installer":

- **Per-user installs.** A great many Windows installers write into a user profile. The agent runs those in the session of the person who asked, not as the service account, and waits until they are signed in rather than installing into a profile nobody uses.
- **Restarts.** Software whose driver loads at boot is not finished when its installer exits. The install stays open, the person is asked to restart, and it settles itself afterwards.
- **Prerequisites.** A catalog app can name the apps that must go on first. One click installs the chain in order, skipping whatever the PC already has.
- **Requirements.** An app can state what it needs in plain words, and the person confirms it before installing. The portal never checks these and never refuses an install over one.
- **Removal.** Software can come off again, by an administrator always and by the person who installed it when the app allows it.
- **Large downloads.** A multi-gigabyte installer resumes after a dropped connection or a service restart, is verified against its hash, and is cached so a second device on the same image does not fetch it twice.

## Updates

The agent keeps the whole installation current, itself included. It asks GitHub for the newest release when the service starts, once a day at a random second in the noon hour, and within ten seconds of a client asking. The random second keeps a site's worth of machines from arriving together. A release newer than what is installed is downloaded as `AppPortal-<version>-x64.msi`, checked against that release's `SHA256SUMS`, and kept under `%ProgramData%\AppPortal\updates`. Anything that does not match its published hash is discarded.

Applying it needs the client closed, because Windows Installer cannot replace files a running process holds open. With nothing open the agent runs `msiexec /i <msi> /qn /norestart /l*v update-<version>.log` as SYSTEM straight away. With a client open it waits, and that client shows **Restart to update**. Older MSIs are deleted after a successful install; a failure keeps the log beside the MSI and is reported rather than retried differently.

`%ProgramData%\AppPortal\update.json` is what the client's banners read: the installed version, the newest published one, the one waiting to be applied, and a result of `UpToDate`, `Available`, `Installed`, `Offline` or `Failed`. The client never reaches the release feed itself. All it can do is leave `update.request` in the same folder, which the agent takes and deletes within ten seconds; the download and the install are the agent's, as SYSTEM. So that a signed-in user can leave that file, the agent grants the Users group the right to add a file to that one folder and nothing else, which leaves `client.json` and `enroll.json` as they were.

Before it runs or stages an MSI, the agent also checks who signed it. The rule starts with the first signed agent: an agent that is itself validly signed installs only an MSI validly signed by the same publisher, and refuses anything else with `Failed` and the reason in `update.json`, leaving the file unrun. An agent that is not signed, which is every release before the first signed one, a build its owner compiled, and a test-signed rehearsal, checks updates by their SHA-256 alone, as before. The publisher is not written into the agent; it is whoever signed the running agent, so a self-hoster who signs with their own certificate gets the same protection. The decision is recorded in [ADR 0002](adr/0002-a-signed-agent-takes-only-signed-updates.md).

Going back to unsigned builds is deliberate work: deploy an unsigned MSI to the fleet once by hand, the same way the fleet was first deployed. After that the agents are unsigned and check updates by SHA-256 alone again. The same applies to a signed fleet pointed at a fork whose releases are unsigned or signed by somebody else.

An `"updateRepository": "owner/name"` in `client.json` points a test fleet at a fork. `AppPortal.Agent.exe --check` prints what that repository publishes, and which signature rule this agent applies, and downloads nothing.

A PC upgraded from a zip installation still carries three things Windows Installer knows nothing about: the **App Portal Updater** scheduled task, `AppPortal.Updater.exe` and `Uninstall-AppPortalClient.ps1` in `%ProgramFiles%\App Portal`, and an `AppPortalClient` entry in Apps & Features. The agent removes all three on its first start. The task and the updater would otherwise go on replacing files Windows Installer now owns. The entry is worse than that: it puts a second **App Portal** row beside the MSI's, and removing through it runs the retired script, which deletes both `%ProgramFiles%\App Portal` and `%ProgramData%\AppPortal` while Windows Installer still holds the product as installed. A machine upgraded before the agent knew to do this is put right the first time it starts an agent that does.

## Reliability notes

A few behaviours are deliberate and were put in after a review found the failure they prevent:

- **One install request per device at a time wins.** `InstallService.CreateAsync` takes a per-device gate, so two overlapping requests cannot both pass the duplicate and concurrency checks and start two deployments.
- **A finished install stays finished.** The background poller and every client refresh update the same record from their own snapshots. `InstallStore.Upsert` drops a write that carries an older `LastCheckedAt` than what is stored, and never moves a terminal state back to active.
- **State is one database, written in transactions.** Catalog, devices and install history live in `app-portal.db` on the data volume. A refresh that would overwrite a newer one is dropped inside the same transaction that read it, so two callers cannot interleave. A file that cannot be opened stops the server at start rather than leaving it serving an empty catalog to every device.
- **The client does not die on a bad response.** An empty body or an unexpected content type, which a reverse proxy can produce, surfaces as a message in the window rather than an unhandled exception from a timer callback. Anything that still escapes is appended to `%LocalAppData%\AppPortal\client.log`.

## Limits

- One catalog for all devices. Per-device or per-group catalogs are not implemented.
- Removal goes through the agent only. Action1 owns what Action1 deployed, so an app whose engine on a PC is Action1 is refused with that reason rather than half-removed.
- The portal installs launchers and applications, not the content a launcher downloads afterwards. That content belongs to a signed-in account inside that launcher, and the portal has no account there.
- winget is not on a service account's PATH, so the agent finds it in the App Installer package directory. A PC without the App Installer cannot use winget packages, and the agent says so rather than failing obscurely.
- A per-user install needs the person who asked to sign in. It waits up to seven days and then gives up, saying whose sign-in it waited for.
- A restart is asked for, never forced. An install that needs one waits until somebody agrees.
- Device tokens do not expire. Rotate them on the device detail page, or run `device add` again for the same name.
- Admin sign-in uses local accounts or, optionally, Active Directory. OpenID Connect and email notifications are not implemented.
- Approving a request never installs anything. The requester's client points to the linked app, and the person installs it from its card.
- Updates come only from GitHub releases over HTTPS, verified by SHA-256. From the first signed agent onwards, an update must also be signed by the publisher that signed the installed agent. That publisher, SignPath Foundation, is shared with every other project the foundation signs, so the check tells a foundation-signed MSI from an unsigned or foreign one, not App Portal from another foundation-signed product. A machine without internet access keeps the build it has.
- Action1's API is rate limited (HTTP 429). The server polls active installs every 30 seconds by default; keep the catalog small and the device count modest.

## Why this exists

Action1 announced a Self-Service App Portal in October 2025 and lists it as an upcoming release on its roadmap. Until it ships, this is the gap-filler for a locked-down workstation where AppLocker allows only what lands in Program Files through the management agent. The client itself installs to Program Files for that reason. The App Portal agent has since made the portal useful without Action1 as well.
