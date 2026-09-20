# M1-11: Optional directory sign-in for administrators

**Milestone:** 1 (0.3.1)
**Depends on:** M1-03
**Unlocks:** nothing; every other package works with local accounts alone

## Goal

A deployment that already runs Active Directory can let its administrators sign in to `/admin` with their
directory account instead of a second password kept in the portal. A deployment that does not want this, or
has no directory at all, changes nothing and notices nothing.

## Context

The Decisions table says the product has no dependency on Active Directory, and that stays true: this is an
add-on, off unless configured, and local accounts remain the only way the portal ships. What changes is that
"no dependency" stops meaning "no support".

Why it is worth building for the first deployment: the portal already records the signed-in Windows account
against every install, so the history names directory users while the only way in to administer it is a
password that exists nowhere else. One account to disable when somebody leaves is the point.

Constraints learned from the first deployment:

- Domain controllers answer LDAPS on 636 with a **self-signed** certificate when the forest has no
  certificate authority. Chain validation cannot work; the deployment must pin the certificate or trust it
  explicitly, and the server must fail closed when it cannot.
- A user's UPN suffix need not match the AD DNS name (`user@example.com` inside `ad.example.com`), so a bare
  user name cannot be turned into a bind name by appending the domain. Down-level `DOMAIN\user` always works
  against AD's simple bind and is what the client already sends with installs.
- `System.DirectoryServices.Protocols` on Linux needs OpenLDAP present in the image.

## Scope

### In

- `Directory` configuration section, disabled by default, validated at startup when enabled.
- LDAPS simple bind against one or more controllers, tried in order, with per-connection timeout.
- Certificate pinning by SHA-256 thumbprint. With no thumbprint configured, ordinary chain validation applies.
- A required group: sign-in succeeds only for members, nested groups included. An empty group is a
  configuration error, not an open door.
- Just-in-time provisioning: a successful directory sign-in creates or reuses an `admins` row with
  `source = 'directory'`, named `DOMAIN\user` to match the requester label on installs.
- Local accounts keep working and are tried first, so the directory being down never locks the portal.
- `/admin/admins` shows each account's source; a directory account cannot be given a local password.
- CLI `admin list` shows the source column.

### Out

- Group-to-role mapping: every administrator is a full administrator, as today.
- Per-group catalogs, OpenID Connect, Kerberos or NTLM single sign-on, directory sign-in for the Windows
  client, and synchronising users the portal has never seen.

## Interface

```sql
-- migration 008
ALTER TABLE admins ADD COLUMN source TEXT NOT NULL DEFAULT 'local';   -- 'local' | 'directory'
```

```jsonc
"Directory": {
  "Enabled": false,
  "Servers": ["dc01.example.com", "dc02.example.com"],   // host, or host:port
  "Port": 636,
  "NetBiosDomain": "EXAMPLE",          // what a bare user name is prefixed with, and the row's name
  "UpnSuffix": "example.com",          // fallback bind form when no NetBIOS domain is set
  "BaseDn": "",                        // defaults to the RootDSE defaultNamingContext
  "RequiredGroup": "APP-AppPortal-Admins",   // sAMAccountName or full DN
  "CertificateThumbprints": ["<sha-256 hex>"],
  "TimeoutSeconds": 10
}
```

Sign-in order for both `/admin/login` and `POST /api/v1/admin/session`: throttle, then the local account
table, then the directory. One failure message covers every outcome.

## Steps

1. Migration, `AdminRecord.Source`, store changes, tests.
2. `DirectoryOptions`, name normalisation, `IDirectoryAuthenticator` and the LDAPS implementation behind it.
3. `AdminSignIn` orchestration, wired into the page and the session API, with a fake authenticator in tests.
4. Admins page column, CLI column, `libldap2` in the runtime image, configuration documentation.

## Acceptance criteria

- With `Directory:Enabled=false` the portal behaves exactly as 0.3.0: no LDAP connection is opened.
- A member of the required group signs in at `/admin/login` and gets a session; the `admins` table gains one
  `source = 'directory'` row named `DOMAIN\user`, and a second sign-in reuses it.
- A valid directory password for a non-member is refused, and so is a wrong password.
- A directory account disabled in the portal cannot sign in even though the directory accepts its password.
- `admin reset-password` and the admins page refuse to set a password on a directory account.
- With the controllers unreachable, local sign-in still works and the failure is logged once, not per attempt.
- A certificate that does not match a configured thumbprint fails the bind.
