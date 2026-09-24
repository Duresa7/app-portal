# Administration

There are two places to administer the portal, and they do the same things:

- **The web admin**, at `/admin` on the server. Use it from any browser, including from a PC that has no App Portal on it.
- **The Admin area of the Windows client.** Choose **Admin** at the foot of the pane and sign in with the same administrator account. Use it on a PC that already runs the client, so you do not have to find the server's address. The client stays signed in for that Windows account until you sign out or the session is revoked; the token is encrypted for that account and is never written to `client.json`.

Both sign in with a local administrator account (or a [directory account](server-setup.md#directory-sign-in-optional)). Browser sessions use cookies and forms require antiforgery tokens. The client uses an `apa_` bearer token from the admin JSON API; device tokens cannot open either. Every action on a web admin page has a counterpart in the client.

| | |
|---|---|
| ![Admin dashboard in the Windows client, light theme](images/client-admin-dashboard-light.png) | ![Admin dashboard in the Windows client, dark theme](images/client-admin-dashboard-dark.png) |
| ![Install history in the Windows client](images/client-admin-installs-light.png) | ![Install history in the Windows client, dark theme](images/client-admin-installs-dark.png) |
| ![Catalog in the Windows client](images/client-admin-catalog-light.png) | ![Catalog in the Windows client, dark theme](images/client-admin-catalog-dark.png) |
| ![Devices in the Windows client](images/client-admin-devices-light.png) | ![Devices in the Windows client, dark theme](images/client-admin-devices-dark.png) |

`AppPortal.exe --demo` opens the client with sample data held in memory; the demo administrator is `admin` with the password `demo`.

## The web admin pages

**Installs** shows fleet-wide history with device, requester, app, engine, state and dates. Filter by device, app, state, requester or date range; the table refreshes every 30 seconds.

![Install history in the admin UI](images/admin-installs.png)

**Catalog** manages approved software. Edits appear in the client on its next refresh, including changes to existing cards.

![Catalog management in the admin UI](images/admin-catalog.png)

**Requests** shows pending and decided software requests. Approve or deny with an optional reason of up to 500 characters. An approval can name the catalog app that answers it, or open the create form prefilled from the request; the requester's client then offers that app. Approval never starts an installation.

![Software requests in the admin UI](images/admin-requests.png)

**Devices** supports renaming, disabling, token rotation and removal. Disabling or rotating a token takes effect on the next API call. Removal is refused while an install is active; afterward, install and request history remains available to administrators. A replacement device does not inherit the retired device's history.

**Enrollment keys** lets administrators create and revoke keys with an expiry, use limit and default engine. The full key appears once.

## Requests from users

The client's **Requests** section accepts up to 500 characters describing the software needed. Each device can have 20 pending requests. The newest request appears immediately after submission; status and administrator reasons refresh with the rest of the client. When an administrator answers a request with a catalog app this PC is offered, the request shows **Show in Apps**, which opens Apps on that app so it installs from its card as usual.

![Requests in the Windows client](images/requests.png)

## Where an app comes from

An administrator adds an app by choosing its **Source** on the catalog page and naming the app there; the page shows only the fields that source needs, and a sentence under the form says what saving will do. The person installing it never sees which source it is.

| Source | What it is for | Installs for |
|---|---|---|
| Action1 | A package from your Action1 Software Repository, on a device with an Action1 endpoint. | Everyone |
| winget | Windows applications, from Microsoft's community repository. The default choice for most software. | Everyone, or one person |
| Microsoft Store | Store applications, by their twelve-character product id. Some need the person signed in to the Store before it grants a licence; say so under Requirements. | One person |
| Direct download | Any installer at a URL, checked against its SHA-256. Game launchers and vendor installers that are in no repository. | Everyone, or one person |
| Chocolatey | Windows applications and tools, from the Chocolatey community repository. The closest of these to winget. | Everyone |
| Scoop | Developer tools, into one person's profile without administrator rights. | One person, or everyone with `--global` |
| npm, Yarn | Node.js command line tools. Needs Node.js on the PC. | Everyone, or one person |
| Bun | Node.js packages through Bun. Needs Bun. | One person |
| pip | Python packages and the tools they bring. Needs Python. | Everyone, or one person |
| Cargo | Rust command line tools, built on the PC. Needs Rust. | One person |
| vcpkg | C and C++ libraries into a build tree. For developer machines, not for applications. | The PC |
| .NET tool | Command line tools published to NuGet. Needs the .NET SDK. | One person |
| PowerShell module | A module from the PowerShell Gallery, for PowerShell 7 or for the Windows PowerShell 5.1 every PC already has. | Everyone, or one person |

An app can have an Action1 package and one agent source at once; the catalog page then asks which to use on a device that could use either. Only Action1 and the next four are ways to put an application in front of everybody on a PC. The rest are for developer workstations: reaching for npm to deploy a web browser is a misunderstanding of what npm is.

The portal installs applications and launchers. What a launcher then downloads for one signed-in account, a game in a Steam library or a Riot client's own updates, belongs to that launcher and that account, and is outside the portal: add Steam or the Riot client to the catalog, not the games inside them.

A package manager has to be on the PC before anything can be installed through it, and none of them are on a fresh Windows install. Add the manager itself to the catalog, from winget or a direct download, and list it under **Requires** on the apps that need it: the portal then installs it first, once, and skips it on every PC that already has it. An install through a manager the PC lacks fails with a sentence naming the manager, rather than an exit code.

A package id goes onto a command line, and npm, Yarn and Scoop are batch files that Windows runs through `cmd.exe`. The catalog therefore accepts only the characters a real package id uses for that manager, and refuses anything a command prompt would read as an instruction. Extra arguments are passed through as the administrator typed them.
