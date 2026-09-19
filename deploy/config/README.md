# Server configuration

`catalog.json` is the list of apps the portal offers. It seeds the database the first time the server starts with an empty catalog. After that the database is what the API serves, so an edit to this file reaches the portal when you import it:

```bash
docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll catalog import /app/config/catalog.json
docker compose -f deploy/compose.yaml exec app-portal dotnet AppPortal.Server.dll catalog export > deploy/config/catalog.json
```

`catalog import` upserts by `id` and leaves apps the file does not mention alone. `catalog export` writes the database back out in this format, which is how you keep the checked-in file current. From 0.3.0 the admin pages edit the catalog directly and this file becomes a backup.

Each entry maps an app to an Action1 Software Repository package:

```json
{
  "id": "google-chrome",
  "name": "Google Chrome",
  "publisher": "Google LLC",
  "description": "Web browser.",
  "category": "Browsers",
  "featured": true,
  "action1": { "packageId": "Google_Google_Chrome_1570243626751_builtin", "version": "latest" },
  "match": { "nameContains": "Google Chrome" }
}
```

- `action1.packageId` is the repository package ID. Find it with `AppPortal.Server packages search chrome`, or read it from the package URL in the Action1 console. Check the whole file with `AppPortal.Server catalog verify`.
- `action1.version` is `latest` or an exact published version.
- `match` tells the portal which installed-software row means "this app is present". It defaults to a case-insensitive `nameContains` on the app name.

This folder is mounted read-only into the container, because the catalog is configuration rather than state.

The device registry is not here. `AppPortal.Server device add` writes to `app-portal.db` in the data directory, `/app/data` in the container, which is a named volume. That one file holds the catalog, the devices and the install history: device names, endpoint IDs and SHA-256 hashes of device tokens, never a plaintext token. The token is printed once when the device is added; keep it in the password manager. Back up the volume, not this folder.

A deployment upgrading from 0.2.x keeps its `devices.json` and `installs.json`: both are imported into the database on the first start of 0.3.0 and then ignored.
