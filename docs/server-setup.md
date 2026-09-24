# Server setup

The server is one Docker container. It holds the catalog, the devices, the install history, the requests and the administrator accounts in one SQLite database on a Docker volume.

## Requirements

- Docker with Compose.
- Optional: an Action1 API credential, if you want the server to install through Action1. A fleet that uses only the App Portal agent does not need one.

A secrets manager whose CLI can render an environment file from references is the comfortable way to keep the credential, but any way of writing three lines into a mode-600 file works.

## Steps

1. **Create the Action1 API credential (optional).** In the Action1 console, go to Configuration, API Credentials. Store the Client ID and Client Secret in your secrets manager together with your organization ID (the `org=` value in the console URL). The server needs `view_endpoints`, `view_software_repository`, `view_installed_software`, `view_automations` and `run_automations`.
2. **Write the environment file.** Copy `deploy/server.env.example` to `deploy/server.env`, which is gitignored, and fill in the three `Action1__` values, either by hand or by rendering the file from your secrets manager's references. Keep it mode 600; it is the only place the credential exists on the host. To choose the address and port the server listens on, copy `deploy/env.example` to `deploy/.env` and set `APP_PORTAL_BIND`.
3. **Start the server**:
   ```bash
   docker compose -f deploy/compose.yaml up -d
   ```
   This pulls `ghcr.io/duresa7/app-portal-server`. Put `APP_PORTAL_VERSION=<version>` in `deploy/.env` to pin a release, or add `--build` to build from the checkout. The checked-in catalog seeds an empty database; its package IDs are examples and must be verified against your Action1 Software Repository.
4. **Create the first administrator**:
   ```bash
   docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll admin add --username admin
   ```
   Enter the password at the prompt. For unattended setup, supply `APPPORTAL_ADMIN_PASSWORD` through the process environment. Open `/admin` on the server, sign in, and manage further accounts under **Admins**.
5. **Prepare the catalog.** Open **Catalog** to add or edit apps, search and verify Action1 packages, or import a JSON catalog. Hide removes an app from the device catalog while retaining its history; delete is refused when installs reference it. Export downloads the current catalog. See [the catalog format](../deploy/config/README.md) and [where an app comes from](administration.md#where-an-app-comes-from).
6. **Create an enrollment key.** Open **Enrollment keys**, choose its expiry and use limit, and copy the key shown once. Give it to `AppPortalSetup.exe` or the MSI when you [install the PCs](deploy-to-pcs.md). The agent exchanges it for a device token at first start. Manual device registration remains available for older clients.

Put the server behind TLS (a reverse proxy or your tunnel) before a device on another network uses it. The device token is a bearer secret.

## Command line

The CLI remains available for scripts:

```bash
docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll catalog import /app/config/catalog.json
docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll catalog export
docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll catalog verify
docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll device add --name OBIPC --endpoint-id <endpoint-id>
```

## Directory sign-in (optional)

The portal needs no directory. A deployment that already runs Active Directory can let administrators sign in with their domain account instead of a second password, by configuring the `Directory` section:

```jsonc
"Directory": {
  "Enabled": true,
  "Servers": ["dc01.ad.example.com", "dc02.ad.example.com"],
  "Port": 636,
  "NetBiosDomain": "EXAMPLE",
  "RequiredGroup": "APP-AppPortal-Admins",
  "CertificateThumbprints": ["<sha-256 of the controller certificate>"],
  "CertificateFile": "/app/config/dc-certs.pem",
  "TimeoutSeconds": 10
}
```

How it behaves:

- **Local accounts are checked first**, so a directory that is unreachable cannot lock you out of your own portal. Keep one local account.
- The bind is **LDAPS only** and uses the signing-in user's own credentials; the server holds no service account.
- Only members of `RequiredGroup` are admitted, nested groups included. There is no default group: leaving it empty stops the server rather than admitting the whole directory.
- A forest with no certificate authority gives its controllers self-signed certificates. List their SHA-256 thumbprints in `CertificateThumbprints`: the server opens the TLS connection and compares the certificate before any password is sent, and refuses a mismatch. On Linux the bind itself is performed by OpenLDAP, which validates separately against `CertificateFile`, a PEM holding the controller certificates; set both. With neither, ordinary chain validation applies and a self-signed certificate is refused.
- The first successful sign-in creates an administrator row named `DOMAIN\user`, matching the requester label on installs. Disable it like any other account; its password stays in the directory and cannot be set here.
- Every administrator is a full administrator. There is no group-to-role mapping, no directory sync, and the Windows client does not use this.

Sign in with `DOMAIN\user`, a UPN, or the bare user name when `NetBiosDomain` is set; all three resolve to the same administrator account. Configure the section through the environment like any other setting, for example `Directory__Enabled=true` and `Directory__Servers__0=dc01.ad.example.com`. The controllers are addressed by name, because that is what their certificates carry, so they must resolve and answer on 636 from inside the container. If the Docker host's resolver does not serve the directory's zone, copy `deploy/compose.override.example.yaml` to `deploy/compose.override.yaml`, fill in the addresses, and pass both files to `docker compose`. Put the certificate file in `deploy/config/`, which is already mounted read-only at `/app/config`.

## Upgrading from 0.2.x

Stop the old container and back up both the data volume and `deploy/config` before starting 0.3.0 or later. Keep the existing volume mounted at `/app/data` and the catalog available at `/app/config/catalog.json` for the first start. The server imports `devices.json`, `installs.json` and the catalog into `app-portal.db`; existing device tokens remain valid. Verify the device list, catalog and install history, then create the first administrator with `admin add`.

After verification, archive the legacy JSON files outside the mounted directories. The config volume no longer needs `catalog.json`: edits now live in SQLite, and explicit CLI or browser imports remain available. Leaving legacy files in place can seed a table again if it becomes empty. Back up the data volume while the server is stopped, including the database and admin authentication keys. To roll back, restore the pre-upgrade backup and old image; 0.2.x does not read SQLite.

The upgrade was checked using a copy of a data volume written by the 0.2.1 server in fake mode. Its original token authenticated after migration, catalog and install records remained available, and a restart produced no duplicate records.
