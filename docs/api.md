# HTTP API reference

This is every HTTP route the App Portal server exposes in 0.8.0: the verb, the path, how the caller authenticates, what it sends and what comes back. The source of truth is the code; the files are named at the start of each section.

The web administration under `/admin` is Razor Pages, not an API. It signs in with a form, keeps an `AppPortal.Admin` cookie, and every form carries an antiforgery token. It is not described here.

## Conventions

### JSON

Request and response bodies are JSON. Property names are camelCase, and on input they are matched without regard to case. Enums travel as strings spelled as their C# names, for example `"Queued"` or `"Pending"`. Timestamps are ISO 8601 with an offset. A field whose type ends in `?` may be `null`.

A field marked optional may be left out and takes the default shown. Every other field is expected. The JSON reader does not refuse a missing field by itself: the field arrives as `null`, `0` or `false`, and the route's own checks decide whether that is acceptable. The status codes below are those checks.

### Authentication

| Scheme | Header | Where |
|---|---|---|
| None | | `/healthz` |
| Enrollment key | `X-Enrollment-Key: ape_...` on the check, `key` in the body on enrollment | `/api/v1/enroll` |
| Device token | `Authorization: Bearer <device token>` | `/api/v1/...` except `/api/v1/enroll` and `/api/v1/admin` |
| Admin token | `Authorization: Bearer apa_...` | `/api/v1/admin/...` except `POST /api/v1/admin/session` |

A device token comes from enrollment, from `POST /api/v1/admin/devices`, from a token rotation, or from the `device add` command. It does not expire. A token of a disabled device is refused.

An admin token comes from `POST /api/v1/admin/session` and lives for 30 days. The `Admin` policy that guards the admin routes also accepts the web administration cookie, so a signed-in browser can call them too.

### Request headers

| Header | Sent by | Meaning |
|---|---|---|
| `X-AppPortal-User` | Client | The signed-in Windows account, as `DOMAIN\user`. Informational: the device token authenticates the call. Longer than 128 characters is cut to 128. Control characters in it are refused with 400. Absent or blank is accepted. |
| `X-AppPortal-Sessions` | Agent, on `GET /api/v1/agent/jobs` | The accounts signed in on the PC now, comma separated. Entries longer than 128 characters are dropped. |
| `X-Enrollment-Key` | Setup wizard, on `GET /api/v1/enroll/check` | The enrollment key to check. A header so that the key stays out of access logs. |

### Errors

Every error the handlers write has one shape, `ErrorMessage`, on the device, agent, enrollment and admin routes alike:

```json
{ "message": "No such install request." }
```

Some answers have no body:

- A body that is not valid JSON, a body missing where one is required, or a query value that does not parse (for example `wait=soon`) is refused by the framework with 400 and no body. A body that is not sent as JSON is refused with 415.
- An admin route called without a valid admin token answers 401 with `WWW-Authenticate: Bearer` and no body.
- The admin sign-in throttle answers 429 with no body.

The device token check runs before routing for every path under `/api/v1` except `/api/v1/admin` and `/api/v1/enroll`. A request there without a valid device token gets 401 with `WWW-Authenticate: Bearer` and `{ "message": "A valid device token is required." }`, whatever the path.

### Limits

| Limit | Value | Where |
|---|---|---|
| Software entries in one report | 5000 | `POST /api/v1/agent/software` |
| Package manager entries in one report | 256 | `POST /api/v1/agent/managers` |
| Account name | 128 characters | `X-AppPortal-User`, `account` on software and managers reports |
| Request text | 500 characters | `POST /api/v1/requests` |
| Undecided requests per device | 20 | `POST /api/v1/requests` |
| Decision reason | 500 characters | `POST /api/v1/admin/requests/{id}/approve` and `/deny` |
| Active installs per device | 3 by default, `Portal:MaxActiveInstallsPerDevice` | `POST /api/v1/installs` |
| Catalog import | 4 MB, counted in bytes | `POST /api/v1/admin/catalog/import` |
| Installer download for hashing | 2 GiB by default, `Catalog:MaxDownloadBytes` | `POST /api/v1/admin/catalog/package/hash` |
| Rows in one admin list | 200 | Every paged admin list |
| Enrollment attempts | 30 per minute per remote address | Both enrollment routes |
| Admin sign-in failures | 10 per user name in 15 minutes | `POST /api/v1/admin/session` |

## Public routes

Sources: `Program.cs`, `Enrollment/EnrollmentEndpoints.cs`.

### GET /healthz

No authentication. Always answers 200 with `{ "status": "ok" }`. The container health check calls it.

### POST /api/v1/enroll

Trades an enrollment key for a device record and a device token. No bearer token.

Body `EnrollRequest`:

| Field | Type | Notes |
|---|---|---|
| `key` | string | The enrollment key, `ape_...` |
| `deviceName` | string | |
| `machineId` | string | Identifies the PC across re-enrollment |
| `action1EndpointId` | string? | Required when the key enrolls for `action1` or `both` |
| `agentVersion` | string? | |

A use of the key is spent only when the enrollment succeeds, in the same transaction that writes the device. A refusal of any kind leaves the key's use count as it was. When several machines race for a key's last uses, each use goes to exactly one of them, and the others get 401.

| Status | When |
|---|---|
| 201 | Enrolled. Body `EnrollResponse`. A PC already known by its machine id, or else by its name, takes a new token under its existing record. |
| 400 | `key`, `deviceName` or `machineId` is missing or blank, or the key enrolls for Action1 and `action1EndpointId` is missing. |
| 401 | The key is unknown, expired, used up or revoked. The answer does not say which. |
| 403 | The device is known and an administrator disabled it. |
| 409 | The device store refused the enrollment. |
| 429 | More than 30 attempts from this address in the current minute. Carries `Retry-After: 60`. |

Response `EnrollResponse`:

| Field | Type | Notes |
|---|---|---|
| `deviceId` | string | |
| `deviceToken` | string | Shown here once. The server keeps only its hash. |
| `deviceName` | string | |
| `engines` | string[] | `action1`, `agent`, or both |

Every attempt is written to the key's audit trail, which `GET /api/v1/admin/keys/{id}/events` reads.

### GET /api/v1/enroll/check

Asks whether a key is usable, without spending it. The key goes in `X-Enrollment-Key`. Not written to the audit trail.

| Status | When |
|---|---|
| 200 | The key is active. Body `EnrollmentCheck`: `{ "engine": "action1" }`, where `engine` is `action1`, `agent` or `both`. |
| 401 | The key is missing, unknown, expired, used up or revoked. |
| 429 | The enrollment rate limit, as above. It is shared with `POST /api/v1/enroll`. |

## Device API

Source: `Api/PortalEndpoints.cs`. Every route needs the device token. The caller is always the device the token belongs to; nothing here reads or changes another device.

### GET /api/v1/catalog

The apps this device can install. An app an administrator has hidden is left out, and so is an app no engine on this device can install.

200 with an array of `CatalogApp`, in catalog order. `engine` on each app is the engine this device would use.

### GET /api/v1/device

The calling device and what Action1 knows about its endpoint.

200 with `DeviceInfo`. When Action1 cannot be reached, `endpointStatus` is `"Unknown"` and `lastSeen` is null; the route does not fail.

### GET /api/v1/device/installed

Software present on the device: what Action1 reports for the endpoint, followed by what the agent reported from winget and from each package manager. Per-user software is included for the account named in `X-AppPortal-User` only. Each row is matched to a catalog app where one matches.

| Status | When |
|---|---|
| 200 | Array of `InstalledApp`. |
| 502 | Action1 refused or could not be reached. A device with no Action1 endpoint does not ask it. |

### GET /api/v1/installs

This device's install and removal history, newest first.

| Query | Type | Notes |
|---|---|---|
| `refresh` | bool | Optional, default `true`. When true, each active install is refreshed from its engine before the list is returned. |

200 with an array of `InstallRequest`.

### GET /api/v1/installs/{id}

One install of this device, refreshed from its engine first.

| Status | When |
|---|---|
| 200 | `InstallRequest`. |
| 404 | No such install, or it belongs to another device. |

### POST /api/v1/installs

Starts an install. When the app lists prerequisites the device lacks, they are installed first, in order, as steps of the same install.

Body `CreateInstallRequest`: `{ "appId": "..." }`.

| Status | When |
|---|---|
| 202 | Started. Body `InstallRequest`, `Location: /api/v1/installs/{id}`. |
| 400 | `appId` is missing or blank. |
| 404 | The app is not in the catalog. |
| 409 | This app is already being installed or removed on this device. |
| 422 | No engine on this device can install the app or one of its prerequisites, or the Action1 package version does not resolve. |
| 429 | The device already has the maximum number of active installs. |
| 502 | Action1 refused the deployment. |

### POST /api/v1/uninstalls

Takes an app off this device. Removal goes through the agent only.

Body `CreateUninstallRequest`: `{ "appId": "..." }`.

| Status | When |
|---|---|
| 202 | Started. Body `InstallRequest` with `kind` `uninstall`, `Location: /api/v1/installs/{id}`. |
| 400 | `appId` is missing or blank. |
| 403 | The app does not allow removal by users; or it was installed for another account (a per-user agent install with a different requester); or Action1 is the engine for this app on this device. |
| 404 | The app is not in the catalog. |
| 409 | This app is already being installed or removed on this device. |
| 422 | No engine on this device can serve the app. |
| 502 | Action1 refused or could not be reached. |

The person may remove an app when `userRemovable` is true and a succeeded install of it exists on this device that was machine-wide, had no requester, or was requested by the same account.

### GET /api/v1/requests

This device's requests for software that is not in the catalog, newest first.

200 with an array of `AppRequest`.

### POST /api/v1/requests

Asks an administrator for software. Approval records a decision only.

Body `CreateAppRequest`: `{ "text": "..." }`.

| Status | When |
|---|---|
| 201 | Created. Body `AppRequest`. No `Location` header: there is no route that reads one request, and `GET /api/v1/requests` lists them all. |
| 400 | `text` is empty after trimming, or longer than 500 characters. |
| 429 | The device already has 20 undecided requests. |

## Agent API

Source: `Agent/AgentEndpoints.cs`. The agent authenticates with the same device token as the client, and every route acts on the device that token belongs to.

### POST /api/v1/agent/heartbeat

Body `AgentHeartbeatRequest`:

| Field | Type | Notes |
|---|---|---|
| `agentVersion` | string | |
| `clientVersion` | string? | Null when no client is installed |
| `osVersion` | string | |
| `bootTime` | DateTimeOffset? | Optional. When sent, installs waiting for a restart before this boot are settled. |

A heartbeat records the agent version and marks the device as having the agent.

| Status | When |
|---|---|
| 200 | `AgentHeartbeatResponse`. |
| 400 | `agentVersion` or `osVersion` is missing or blank. |

`AgentHeartbeatResponse` has `serverTime` (DateTimeOffset) and `heartbeatSeconds` (int). `heartbeatSeconds` is 60 while the device has a queued job, and otherwise `Agent:HeartbeatSeconds`, 900 by default. A value of zero or less is answered as 900.

### GET /api/v1/agent/jobs

Leases the next job for this device, waiting for one if asked.

| Query | Type | Notes |
|---|---|---|
| `wait` | int | Optional, default 0. Seconds to wait for a job, clamped to 0 through 25. |

Send `X-AppPortal-Sessions` on every call. A per-user job waits until its requester is signed in, and this header is how the server learns that.

| Status | When |
|---|---|
| 200 | `AgentJob`. The job is leased to this device. |
| 204 | No job arrived within `wait`. |

`AgentJob`:

| Field | Type | Notes |
|---|---|---|
| `id` | string | The job id for the progress and complete routes |
| `installId` | string | |
| `definition` | `PackageDefinition` | What to install; see [Package definitions](#package-definitions) |
| `attempt` | int | Pass it back as `?attempt=` |
| `requester` | string? | The account that asked. For a per-user package, whose profile it goes into. |
| `kind` | string | `install` or `uninstall` |

A job is handed out at most three times before its install is called failed.

### POST /api/v1/agent/jobs/{id}/progress

| Query | Type | Notes |
|---|---|---|
| `attempt` | int | Optional. When sent, a report for any other attempt is refused. |

Body `AgentJobProgress`:

| Field | Type | Notes |
|---|---|---|
| `state` | string | `queued`, `downloading`, `installing`, `waiting_for_user` or `cancelled` |
| `percent` | int | 0 to 100 |
| `detail` | string? | A sentence for the person |

| Status | When |
|---|---|
| 204 | Written, or ignored because it would move the job backwards (for example `downloading` after `installing`). |
| 400 | The state is not one of the five, or the percent is outside 0 to 100. |
| 409 | The job is not leased to this device on this attempt. The agent must stop. |

### POST /api/v1/agent/jobs/{id}/complete

`attempt` as on the progress route.

Body `AgentJobCompletion`:

| Field | Type | Notes |
|---|---|---|
| `ok` | bool | |
| `detail` | string? | Default "Installed." or "Installation failed." |
| `exitCode` | int? | Appended to the detail of a failure |
| `needsRestart` | bool | Optional, default `false`. The software is on the PC but works only after a restart; the install stays running until the next heartbeat with a later `bootTime`. |

| Status | When |
|---|---|
| 204 | Recorded. |
| 409 | The job is not leased to this device on this attempt. |

### POST /api/v1/agent/software

Replaces the device's installed-software list for one account and one source. Other accounts and other sources are not touched.

| Query | Type | Notes |
|---|---|---|
| `account` | string | Optional. Absent is the machine-wide list; present is that account's profile. At most 128 characters. |
| `source` | string | Optional. `winget` or a [package manager name](#package-managers). Absent or blank means `winget`, which is all an agent from before 0.7.0 sends. |

Body: an array of `InstalledSoftware`, each `{ "name": "...", "version": "..." }`.

| Status | When |
|---|---|
| 204 | Replaced. |
| 400 | More than 5000 entries, `account` longer than 128 characters, or a `source` the server does not know. |

### POST /api/v1/agent/managers

Replaces the device's list of package managers.

Body: an array of `DeviceManager`:

| Field | Type | Notes |
|---|---|---|
| `name` | string | |
| `version` | string | |
| `account` | string? | Optional. Null for a manager every account can use; the account name for one that lives in a profile. |

| Status | When |
|---|---|
| 204 | Replaced. |
| 400 | More than 256 entries, or an `account` longer than 128 characters. |

## Admin session

Source: `Admin/AdminAuth.cs`. These two routes issue and revoke the admin token that the rest of the admin API uses.

### POST /api/v1/admin/session

No authentication. Body `SignInRequest`:

| Field | Type | Notes |
|---|---|---|
| `username` | string | A local account, or with directory sign-in configured, `DOMAIN\user`, a UPN or a bare user name |
| `password` | string | |
| `deviceName` | string? | Optional. A label for the sessions list. It authenticates nothing. |

| Status | When |
|---|---|
| 200 | `SignInResponse`: `token` (string, `apa_...`) and `expiresAt` (DateTimeOffset, 30 days on). |
| 401 | The user name and password do not match an enabled administrator. |
| 429 | Ten failed attempts for this user name in the last 15 minutes. The password is not checked. No body. |

The throttle counts per user name, is held in memory, and is cleared by a successful sign-in.

### DELETE /api/v1/admin/session

Admin token. Revokes the session that made the call. Answers 204.

## Admin JSON API

Sources: `Admin/Api/*.cs`. Every route needs the admin token and is written to the log with the administrator's name, the verb and the path. Bodies and query strings are not logged. Every route here has a counterpart in the web administration, except the sessions list.

### Paged lists

The list routes marked *paged* read two query parameters and answer with `AdminPage<T>`.

| Query | Type | Notes |
|---|---|---|
| `limit` | int | Optional, default 50. Clamped to 1 through 200. |
| `offset` | int | Optional, default 0. A negative value is read as 0. |

A value that is not an integer is replaced by the default. There is no sort parameter; each list has a fixed order, given with the route.

`AdminPage<T>`:

| Field | Type | Notes |
|---|---|---|
| `items` | T[] | |
| `offset` | int | |
| `limit` | int | The limit the server used, which may be smaller than the one asked for |
| `hasMore` | bool | More rows follow this page |
| `total` | int? | Rows matching the filter, across all pages |

Filter parameter names are matched without regard to case.

### Sessions

#### GET /api/v1/admin/sessions

Every session the calling administrator holds, web and API, newest first. Not paged.

200 with an array of `AdminSessionSummary`:

| Field | Type | Notes |
|---|---|---|
| `id` | string | |
| `deviceName` | string? | |
| `kind` | string | `web` or `api` |
| `createdAt` | DateTimeOffset | |
| `expiresAt` | DateTimeOffset | |
| `lastUsedAt` | DateTimeOffset? | |
| `current` | bool | This session made the call |

#### DELETE /api/v1/admin/sessions/{id}

Revokes one of the caller's own sessions.

| Status | When |
|---|---|
| 204 | Revoked. |
| 404 | The caller has no session with that id. A session of another administrator also answers 404. |

### Dashboard

#### GET /api/v1/admin/dashboard

200 with `DashboardCounts`:

| Field | Type | Notes |
|---|---|---|
| `devices` | int | Every device |
| `installsToday` | int | Installs requested since midnight in the server's time zone |
| `failuresThisWeek` | int | Installs in `Failed` requested in the last seven days |
| `activeNow` | int | Installs in `Queued` or `Running` |
| `pendingRequests` | int | Undecided requests |

### Installs

#### GET /api/v1/admin/installs

Paged, newest first. `AdminPage<AdminInstall>`. Filters combine with AND:

| Query | Notes |
|---|---|
| `device` | Device name, exact, any case |
| `app` | Catalog app id, exact, any case |
| `state` | An `InstallState` name, any case. Absent or blank is every state. |
| `requester` | Substring of the requester, any case |
| `from` | A day, `yyyy-MM-dd`. From midnight of that day, server time. |
| `to` | A day, `yyyy-MM-dd`. Up to the end of that day, server time. |
| `restart` | `1` for installs waiting for a restart |

400 when `state` is not one of the five names. A number such as `1` and a list such as `Failed,Succeeded` are refused too.

#### GET /api/v1/admin/installs/{id}

| Status | When |
|---|---|
| 200 | `AdminInstall`. |
| 404 | No such install. |

#### POST /api/v1/admin/installs/{id}/cancel

Stops an active agent install and every unfinished step of it. No body.

| Status | When |
|---|---|
| 200 | `AdminInstall`, as it stands after the cancel. |
| 404 | No such install. |
| 409 | The install has finished, its engine is not the agent, or its job could not be stopped. |

### Requests

#### GET /api/v1/admin/requests

Paged, newest first. `AdminPage<AdminRequest>`.

| Query | Notes |
|---|---|
| `status` | `pending`, `approved`, `denied` or `all`, any case. Absent or blank is `all`. |

400 when `status` is anything else. A number such as `1` and a list such as `Pending,Approved` are refused too.

#### POST /api/v1/admin/requests/{id}/approve

#### POST /api/v1/admin/requests/{id}/deny

Records a decision on a pending request. The two routes behave the same apart from the decision they record. Approval does not add an app to the catalog or start an install.

Body `AdminDecision`, which may be left out: `{ "reason": "..." }`. `reason` is optional and reaches the person who asked. A blank reason is stored as none.

| Status | When |
|---|---|
| 200 | `AdminRequest`, decided. |
| 400 | The reason is longer than 500 characters after trimming. |
| 404 | No such request. |
| 409 | The request had already been decided. The earlier decision stands. |

### Catalog

Every catalog write runs the same checks: `PUT`, the import below, the web edit form, the import on the web catalog page, and `AppPortal.Server catalog import`. An app one of them refuses, all of them refuse, with the same message.

#### GET /api/v1/admin/catalog

Paged, in catalog order. `AdminPage<AdminCatalogApp>`. Hidden apps are included.

| Query | Notes |
|---|---|
| `search` | Substring of the id, name, publisher or category, any case |

#### GET /api/v1/admin/catalog/{id}

| Status | When |
|---|---|
| 200 | `AdminCatalogApp`. |
| 404 | No app with that id. |

`GET /api/v1/admin/catalog/export` is a route of its own, so no write accepts the id `export`. An app stored under that id before the rule stays in the list, the export and the device catalog, and can be hidden or deleted by id, but it cannot be read here and cannot be saved again under that id.

#### PUT /api/v1/admin/catalog/{id}

Creates the app or replaces it. The id in the path is the app's id; an `id` in the body is ignored. Every field comes from the body, so a field left out takes its default.

Body `AdminCatalogApp`. In this body `kind` must be the first property of `agent`; with `kind` anywhere else the definition is not read and the request fails. The catalog import accepts `kind` in any position.

| Status | When |
|---|---|
| 200 | `AdminCatalogApp`, as stored. The same code for a create and a replace. |
| 400 | The id is `new` (the web create form) or `export` (the export route), in any case; the name is blank; `requirements` is longer than 500 characters; `engineOverride` is not `action1`, `agent` or empty; the app has neither an Action1 package id nor an agent package; or the agent package fails its checks. |
| 422 | A `requires` entry is not in the catalog, or the prerequisites would form a loop, an app that needs itself included. |

#### DELETE /api/v1/admin/catalog/{id}

| Status | When |
|---|---|
| 204 | Deleted, with its packages. |
| 404 | No app with that id. |
| 409 | Installs refer to the app. Hide it instead. |

#### POST /api/v1/admin/catalog/{id}/hidden

Body `AdminCatalogHidden`: `{ "hidden": true }`. A hidden app is not offered to devices and keeps its history.

| Status | When |
|---|---|
| 200 | `AdminCatalogApp`. |
| 404 | No app with that id. |

#### POST /api/v1/admin/catalog/import

The body is a catalog file, the same shape `GET /api/v1/admin/catalog/export` writes. The content type is not checked.

```json
{ "apps": [ { "id": "...", "name": "...", "action1": { "packageId": "...", "version": "latest" } } ] }
```

Each entry has the fields of `AdminCatalogApp`. Comments and trailing commas are allowed, and `kind` may appear anywhere in an agent definition. Apps are written by id: an app in the file replaces the stored one in every field, `hidden` included, so an entry without `hidden` makes the app visible. Apps the file does not name are left alone. The whole file is checked before anything is written, and written in one transaction, so one refused entry writes none of the file.

The prerequisites are checked on the catalog as the file would leave it. A `requires` entry may name an app later in the same file or one already in the catalog, and an app the file names takes its `requires` from the file.

| Status | When |
|---|---|
| 200 | `AdminCatalogImported`: `{ "imported": 3 }`. |
| 400 | The body is empty; it is not valid JSON; an id appears twice; or an entry fails a check `PUT` answers with 400: a reserved id, no name, a `requirements` note longer than 500 characters, an unknown `engineOverride`, neither an Action1 package id nor an agent package, or an agent definition with no kind or that fails its checks. |
| 422 | A `requires` entry names an app that is neither in the file nor in the catalog, or the prerequisites would form a loop. |
| 413 | The body is larger than 4 MB (4,194,304 bytes as sent, not characters). |

#### GET /api/v1/admin/catalog/export

200 with the whole catalog as a catalog file, indented, `Content-Type: application/json`. Null fields are left out. The file imports again unchanged.

#### POST /api/v1/admin/catalog/action1/search

Searches the Action1 Software Repository. Body `AdminPackageSearch`: `{ "term": "..." }`; `term` is optional.

| Status | When |
|---|---|
| 200 | Array of `AdminPackageResult`: `id`, `name`, `vendor` (strings) and `builtin` (bool). |
| 502 | Action1 refused or could not be reached. |

#### POST /api/v1/admin/catalog/action1/verify

Checks that a package and version resolve in the Software Repository. Body `AdminPackageRef`; `packageId` is required, `version` is optional and defaults to `latest`, `source` is ignored.

| Status | When |
|---|---|
| 200 | `AdminPackageVerified`: `ok` (bool) and `message` (string). `ok` is false when there is no such package or version. |
| 400 | `packageId` is missing or blank. |
| 502 | Action1 could not be reached. |

#### POST /api/v1/admin/catalog/package/hash

Downloads an installer and reports its hash and size, for a direct package. Body `AdminInstallerRequest`: `{ "url": "..." }`.

| Status | When |
|---|---|
| 200 | `AdminInstallerHash`: `sha256` (string) and `sizeBytes` (long). |
| 400 | The URL is not absolute HTTP or HTTPS, or carries credentials; the download failed, took longer than 30 minutes, was empty, or is larger than the limit. |

#### POST /api/v1/admin/catalog/package/winget

Looks a winget package up in the winget-pkgs repository. Body `AdminPackageRef`; `packageId` is required, `source` is optional (`winget` or `msstore`, default `winget`), `version` is ignored.

| Status | When |
|---|---|
| 200 | `AdminWingetLookup`: `exists` (bool?) and `message` (string). `exists` is null when the lookup could not tell, and always null for `msstore`, which the server cannot check. |
| 400 | The id does not have the form the source needs, or the source is neither `winget` nor `msstore`. |

### Devices

#### GET /api/v1/admin/devices

Paged, by name. `AdminPage<AdminDevice>`.

| Query | Notes |
|---|---|
| `search` | Substring of the device name, any case |

#### GET /api/v1/admin/devices/{id}

| Status | When |
|---|---|
| 200 | `AdminDeviceDetail`: the device, its 20 most recent installs, its 20 most recent requests, and its package managers. |
| 404 | No such device. |

#### POST /api/v1/admin/devices

Registers a device by hand and issues its token. Body `AdminDeviceCreate`:

| Field | Type | Notes |
|---|---|---|
| `name` | string | |
| `action1EndpointId` | string? | Optional |

| Status | When |
|---|---|
| 201 | `AdminDeviceToken`, `Location: /api/v1/admin/devices/{id}`. The token is shown here once. |
| 400 | The name is blank. |
| 409 | A device of that name exists. Rotate its token instead. |

#### PUT /api/v1/admin/devices/{id}

Replaces the editable fields. Body `AdminDeviceUpdate`:

| Field | Type | Notes |
|---|---|---|
| `name` | string | |
| `action1EndpointId` | string? | Optional. Left out or blank clears it. |
| `enabled` | bool | Optional, default `true`. A disabled device's token is refused from its next call. |
| `enginePreference` | string? | Optional. `action1` or `agent`, any case. Left out or blank follows the app and the server; there is no keyword for that, so `inherit` is refused. |

| Status | When |
|---|---|
| 200 | `AdminDevice`. |
| 400 | The name is blank, or the engine preference is not blank, `action1` or `agent`. |
| 404 | No such device. |
| 409 | Another device has the name, or the Action1 endpoint would change while an install is in progress. |

#### POST /api/v1/admin/devices/{id}/rotate-token

Issues a new token. The old one stops working at once. No body.

| Status | When |
|---|---|
| 200 | `AdminDeviceToken`. Shown here once. |
| 404 | No such device. |

#### DELETE /api/v1/admin/devices/{id}

Removes the device. Its install and request history stays.

| Status | When |
|---|---|
| 204 | Removed. |
| 404 | No such device. |
| 409 | The device has an install queued or running. |

### Enrollment keys

#### GET /api/v1/admin/keys

Paged, newest first. `AdminPage<EnrollmentKeySummary>`. No filter.

#### POST /api/v1/admin/keys

Body `EnrollmentKeyCreate`:

| Field | Type | Notes |
|---|---|---|
| `name` | string | |
| `engine` | string | Optional, default `action1`. `action1`, `agent` or `both`, any case. |
| `expiresAt` | DateTimeOffset? | Optional. Null never expires. |
| `maxUses` | int? | Optional. Null has no limit. |

| Status | When |
|---|---|
| 201 | `EnrollmentKeyCreated`: `key` (`EnrollmentKeySummary`) and `plaintext` (string). The plaintext is in this reply only. `Location: /api/v1/admin/keys/{id}`. |
| 400 | The name is blank, the engine is not one of the three, `maxUses` is below 1, or `expiresAt` has passed. |

#### GET /api/v1/admin/keys/{id}

| Status | When |
|---|---|
| 200 | `EnrollmentKeySummary`. |
| 404 | No such key. |

#### POST /api/v1/admin/keys/{id}/revoke

No body. Revoking a revoked key succeeds.

| Status | When |
|---|---|
| 200 | `EnrollmentKeySummary`. |
| 404 | No such key. |

#### GET /api/v1/admin/keys/{id}/events

The key's audit trail, newest first. Not paged, and the answer is a plain array, not an `AdminPage`: it is the newest `limit` attempts (default 50, at most 200, read as the paged lists read it) and there is no way to reach older ones. The key detail page shows the newest 50 the same way.

| Status | When |
|---|---|
| 200 | Array of `EnrollmentKeyEvent`. |
| 400 | `offset` is above 0. |
| 404 | No such key. |

### Administrators

#### GET /api/v1/admin/admins

Paged, by user name. `AdminPage<AdminAccount>`. No filter.

#### POST /api/v1/admin/admins

Creates a local administrator. Body `AdminAccountCreate`: `username` and `password`, both strings.

| Status | When |
|---|---|
| 201 | `AdminAccount`. No `Location` header: there is no route that reads one administrator. |
| 400 | The user name is blank, longer than 64 characters or contains control characters; the password is shorter than 12 characters; or the name is taken. |

#### POST /api/v1/admin/admins/{id}/disable

No body. Disables the account and revokes every session it holds. There is no route to enable an account again.

| Status | When |
|---|---|
| 200 | `AdminAccount`. |
| 404 | No such administrator. |
| 409 | The account is the caller's own, or it is the only enabled administrator. |

#### POST /api/v1/admin/admins/{id}/reset-password

Body `AdminPasswordReset`: `{ "password": "..." }`. Sets the password and revokes every session the account holds, the caller's own included when it is the caller's account.

| Status | When |
|---|---|
| 204 | Changed. |
| 400 | The account signs in through the directory, or the password is shorter than 12 characters. |
| 404 | No such administrator. |

### Settings

#### GET /api/v1/admin/settings

200 with `AdminSettings`: `{ "defaultEngine": "action1" }`.

#### PUT /api/v1/admin/settings

Body `AdminSettings`. `defaultEngine` is `action1` or `agent`, any case.

| Status | When |
|---|---|
| 200 | `AdminSettings`, as stored, in lower case. |
| 400 | Anything else. |

## Types

The records below are in `AppPortal.Shared`: `Contracts.cs`, `AdminContracts.cs` and `Packages.cs`.

### Device and agent types

`CatalogApp`:

| Field | Type | Notes |
|---|---|---|
| `id` | string | |
| `name` | string | |
| `publisher` | string | |
| `description` | string | |
| `category` | string | |
| `iconUrl` | string? | |
| `featured` | bool | |
| `engines` | string[] | The engines the app has a package for: `action1`, `agent` |
| `downloadSizeBytes` | long? | Known for a direct package only |
| `installScope` | string? | `machine` or `user`, from the agent package; null without one |
| `engine` | string? | The engine this device would use |
| `requirements` | string? | Words for the person to read before installing |
| `userRemovable` | bool | |

`DeviceInfo`: `deviceName` (string), `endpointId` (string), `endpointStatus` (string), `lastSeen` (DateTimeOffset?).

`InstalledApp`:

| Field | Type | Notes |
|---|---|---|
| `name` | string | |
| `vendor` | string | Empty for software only the agent saw |
| `version` | string | |
| `catalogAppId` | string? | The catalog app it matches |
| `source` | string | `action1`, `winget`, or a package manager name |

`InstallRequest`:

| Field | Type | Notes |
|---|---|---|
| `id` | string | |
| `appId` | string | |
| `appName` | string | |
| `deviceName` | string | |
| `requestedAt` | DateTimeOffset | |
| `completedAt` | DateTimeOffset? | |
| `state` | `InstallState` | `Queued`, `Running`, `Succeeded`, `Failed` or `Cancelled` |
| `percentComplete` | int | |
| `detail` | string? | |
| `requestedBy` | string? | From `X-AppPortal-User` |
| `engine` | string? | `action1` or `agent` |
| `rebootState` | string? | Null, `pending` while a restart is owed, `confirmed` after it |
| `stepName` | string? | The app the current step installs |
| `stepNumber` | int | |
| `stepCount` | int | 1 unless the app has prerequisites to install first |
| `kind` | string | `install` or `uninstall` |

`AppRequest`:

| Field | Type | Notes |
|---|---|---|
| `id` | string | |
| `text` | string | |
| `deviceName` | string | |
| `requestedBy` | string? | |
| `status` | `AppRequestStatus` | `Pending`, `Approved` or `Denied` |
| `reason` | string? | The administrator's reason |
| `createdAt` | DateTimeOffset | |
| `decidedAt` | DateTimeOffset? | |

### Admin types

`AdminInstall`: `id`, `appId`, `appName` (strings), `deviceId` (string?), `deviceName`, `endpointId` (strings), `requestedBy` (string?), `engine`, `kind` (strings), `state` (`InstallState`), `percentComplete` (int), `detail`, `rebootState`, `stepName` (string?), `stepNumber`, `stepCount` (int), `requestedAt` (DateTimeOffset), `completedAt`, `lastCheckedAt` (DateTimeOffset?), `automationId` (string?, the Action1 automation or agent job behind the current step).

`AdminRequest`: the fields of `AppRequest` plus `decidedBy` (string?), the administrator who decided.

`AdminCatalogApp`:

| Field | Type | Notes |
|---|---|---|
| `id` | string | Ignored on `PUT`; the path gives the id |
| `name` | string | |
| `publisher` | string | Optional, default `""` |
| `description` | string | Optional, default `""` |
| `category` | string | Optional, default `Other` |
| `iconUrl` | string? | Optional |
| `featured` | bool | Optional, default `false` |
| `hidden` | bool | Optional, default `false` |
| `engineOverride` | string? | Optional. `action1` or `agent` for an app both could install; null or blank follows the server. Any case is accepted and stored in lower case; any other value is refused. |
| `requirements` | string? | Optional. At most 500 characters. |
| `requires` | string[]? | Optional. Ids of catalog apps to install first, in order. |
| `userRemovable` | bool | Optional, default `false` |
| `match` | `AdminMatchRule`? | Optional. `nameContains` and `nameEquals` (string?), matched against installed software names. |
| `action1` | `AdminAction1Package`? | Optional. `packageId` (string) and `version` (string, default `latest`). A blank `packageId` means no Action1 package. |
| `agent` | `PackageDefinition`? | Optional. See below. |

An app needs an Action1 package id, an agent package, or both.

`AdminDevice`:

| Field | Type | Notes |
|---|---|---|
| `id` | string | |
| `name` | string | |
| `endpointId` | string | The Action1 endpoint id, or empty |
| `enabled` | bool | |
| `hasAgent` | bool | Set by enrollment or by the first heartbeat |
| `enginePreference` | string? | |
| `agentVersion` | string? | |
| `enrolledWithKeyId` | string? | |
| `enrolledWithKeyName` | string? | |
| `machineId` | string? | |
| `createdAt` | DateTimeOffset | |
| `lastSeenAt` | DateTimeOffset? | |
| `installCount` | int | |

`AdminDeviceDetail`: `device` (`AdminDevice`), `recentInstalls` (`AdminInstall[]`), `recentRequests` (`AdminRequest[]`), `managers` (`DeviceManager[]`?).

`AdminDeviceToken`: `deviceId` and `deviceToken`, strings. The server keeps only the token's SHA-256.

`EnrollmentKeySummary`:

| Field | Type | Notes |
|---|---|---|
| `id` | string | |
| `name` | string | |
| `keyPrefix` | string | The eight characters after `ape_`, to recognise a key by |
| `defaultEngine` | string | `action1`, `agent` or `both` |
| `status` | string | `active`, `expired`, `exhausted` or `revoked` |
| `expiresAt` | DateTimeOffset? | |
| `maxUses` | int? | |
| `uses` | int | |
| `revokedAt` | DateTimeOffset? | |
| `createdBy` | string | |
| `createdAt` | DateTimeOffset | |

`EnrollmentKeyEvent`: `id` (string), `deviceId`, `deviceName` (string?), `source` (string, the remote address), `outcome` (string: `enrolled`, `re-enrolled`, `key-refused`, `rejected` or `device-disabled`), `description` (string), `createdAt` (DateTimeOffset).

`AdminAccount`: `id`, `username` (strings), `disabled` (bool), `source` (string, `local` or `directory`), `createdAt` (DateTimeOffset), `lastLoginAt` (DateTimeOffset?). The password hash is never sent.

### Package definitions

`PackageDefinition` is what the agent installs. It is polymorphic on `kind`, which is `winget`, `direct` or `managed`. It is the `agent` field of a catalog app and the `definition` of an agent job.

The server writes `kind` as the first property. On `POST /api/v1/admin/catalog/import` and in the seed catalog, `kind` may appear anywhere in the object. On `PUT /api/v1/admin/catalog/{id}` it must come first.

Every kind has `scope`, which is `machine` (runs as SYSTEM, for everyone on the PC) or `user` (runs in the requester's session, into their profile), and `requiresReboot` (bool, optional, default `false`).

**`winget`** (`WingetPackageDefinition`):

| Field | Type | Notes |
|---|---|---|
| `id` | string | For `winget`, a dotted id such as `Valve.Steam`. For `msstore`, the twelve-character product id. |
| `scope` | string | `machine` or `user` |
| `version` | string? | Optional. Always written, as null when unset. |
| `extraArgs` | string? | Optional. Always written, as null when unset. |
| `requiresReboot` | bool | Optional |
| `source` | string | Optional, default `winget`. `winget` or `msstore`. |

**`direct`** (`DirectPackageDefinition`):

| Field | Type | Notes |
|---|---|---|
| `url` | string | Absolute HTTP or HTTPS, without credentials |
| `sha256` | string | 64 hexadecimal characters |
| `installerType` | string | `msi`, `exe` or `msix`. Nullsoft, Inno Setup and other installers are `exe`. |
| `silentArgs` | string | Required for `msi`, which gets `/qn /norestart` in addition. May be empty for `exe` and `msix`. |
| `sizeBytes` | long | Greater than zero |
| `uninstallKey` | string? | Optional. Always written, as null when unset. |
| `scope` | string | Optional, default `machine` |
| `requiresReboot` | bool | Optional |

**`managed`** (`ManagedPackageDefinition`):

| Field | Type | Notes |
|---|---|---|
| `manager` | string | A name from the table below |
| `id` | string | Only the characters that manager's ids use; see below |
| `scope` | string | One of the scopes the manager supports |
| `version` | string? | Optional. Letters, digits, `.`, `+`, `_`, `-`, starting with a letter or digit. Refused for a manager that cannot pin a version. Always written. |
| `extraArgs` | string? | Optional. Passed through as written. Always written. |
| `requiresReboot` | bool | Optional |

### Package managers

`PackageManagers.All` in `PackageManagers.cs` is the only list of names. The `manager` field of a managed package and the `source` query of `POST /api/v1/agent/software` accept exactly these.

| Name | Display name | Scopes | Pins a version | Package id |
|---|---|---|---|---|
| `choco` | Chocolatey | machine | yes | one word |
| `scoop` | Scoop | user, machine | yes | scoped |
| `npm` | npm | machine, user | yes | scoped |
| `yarn` | Yarn | machine, user | yes | scoped |
| `bun` | Bun | user | yes | scoped |
| `pip` | pip | machine, user | yes | one word |
| `cargo` | Cargo | user | yes | one word |
| `vcpkg` | vcpkg | machine | no | port |
| `dotnet-tool` | .NET tool | user | yes | one word |
| `powershell-module` | PowerShell module | machine, user | yes | one word |
| `powershell5-module` | Windows PowerShell module | machine, user | yes | one word |

The id rules are allowlists, because several of these managers are batch files that `cmd.exe` reads:

- **one word**: a letter or digit, then letters, digits, `.`, `_`, `+` and `-`.
- **scoped**: the same without `+`, with an optional leading `@` and one optional `/` segment, as in `@scope/name` or `bucket/app`.
- **port**: lower-case letters, digits and `-`, with an optional feature list in square brackets, as in `curl[ssl,http2]`.
