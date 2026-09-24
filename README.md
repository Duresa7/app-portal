# App Portal

[![Latest release](https://img.shields.io/github/v/release/Duresa7/app-portal)](https://github.com/Duresa7/app-portal/releases/latest)
[![CI](https://github.com/Duresa7/app-portal/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Duresa7/app-portal/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A self-service app store for Windows PCs. Your users pick software from a list you approve, and App Portal installs it for them. They do not need administrator rights.

![App Portal in light theme](docs/images/apps-light.png)

## Features

- **One-click installs** from a catalog that you control.
- **No administrator rights** on the PC. The install runs as SYSTEM.
- **Many sources:** winget, Microsoft Store, direct downloads, Chocolatey, Scoop, npm, pip, and [more](docs/administration.md#where-an-app-comes-from).
- **Works with or without Action1.** Use the built-in agent, [Action1](https://www.action1.com/), or both.
- **Software requests.** Users ask for apps that are not in the catalog. You approve or deny them.
- **Admin in the browser or in the app:** catalog, devices, requests and install history.
- **Handles the hard parts:** per-user installs, restarts, prerequisites, uninstall and large downloads.
- **Updates itself** from GitHub releases.
- **Windows 11 look** with light and dark themes.

| Dark theme | Activity |
|---|---|
| ![Apps in dark theme](docs/images/apps-dark.png) | ![Activity](docs/images/activity.png) |

## Try the demo

The demo shows the full app with sample data. It installs nothing and sends nothing. You need the [.NET SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/Duresa7/app-portal.git
cd app-portal
dotnet run --project src/AppPortal.Client -- --demo
```

To see the admin area in the demo, sign in as `admin` with the password `demo`.

## Get started

### 1. Start the server

You need Docker.

```bash
git clone https://github.com/Duresa7/app-portal.git
cd app-portal
cp deploy/server.env.example deploy/server.env
docker compose -f deploy/compose.yaml up -d
docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll admin add --username admin
```

If you use Action1, put your Action1 API credential in `deploy/server.env` before you start the server. Put the server behind HTTPS before PCs on other networks use it.

### 2. Add apps and an enrollment key

Open `http://<your-server>:8080/admin` and sign in.

- Go to **Catalog** and add the apps that users can install.
- Go to **Enrollment keys** and make a key. Copy it. The server shows the key one time only.

### 3. Install App Portal on your PCs

Download from the [latest release](https://github.com/Duresa7/app-portal/releases/latest):

- **One PC:** run `AppPortalSetup.exe`. Enter the server address and the enrollment key.
- **Many PCs:** deploy the MSI with Group Policy, Intune or your RMM:

  ```powershell
  msiexec /i AppPortal-<version>-x64.msi /qn SERVERURL=https://portal.example.com ENROLLMENTKEY=ape_...
  ```

The PC enrolls itself, and App Portal shows in the Start menu. To check a download before you deploy it, see [Checking a download](docs/deploy-to-pcs.md#checking-a-download).

## Documentation

| Guide | What it tells you |
|---|---|
| [Server setup](docs/server-setup.md) | All server settings, Action1, Active Directory sign-in, upgrades |
| [Administration](docs/administration.md) | The admin pages, requests, and the app sources |
| [Installing on PCs](docs/deploy-to-pcs.md) | Setup.exe and MSI options, exit codes, checking a download |
| [How it works](docs/how-it-works.md) | Architecture, install engines, updates, known limits |
| [Development](docs/development.md) | Building, testing and releasing |
| [API reference](docs/api.md) | Every HTTP route on the server |
| [Roadmap](docs/ROADMAP.md) | What comes next |

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: the repository's maintainers, [Duresa7](https://github.com/Duresa7). A change from anybody else arrives as a pull request and is reviewed by a maintainer before it is merged.
- Approvers: [Duresa7](https://github.com/Duresa7), who approves every release signing request by hand.

What is signed: `AppPortal.exe`, `AppPortal.dll`, `AppPortal.Shared.dll`, `AppPortal.Agent.exe`, the MSI and `AppPortalSetup.exe`: the project's own files and nothing a third party published.

Privacy: App Portal sends data only to systems its administrator configures: the App Portal server named at install, GitHub's release feed for updates, and the package sources in the catalog. It sends nothing to the project's authors or to SignPath.

## License

MIT. See [LICENSE](LICENSE). Action1 is a trademark of Action1 Corporation. This project is not affiliated with or endorsed by Action1.
