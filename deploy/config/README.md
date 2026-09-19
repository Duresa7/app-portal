# Server configuration

`catalog.json` is the list of apps the portal offers. Edit it and the running server picks the change up on the next request; no restart needed. Each entry maps an app to an Action1 Software Repository package:

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

The device registry is not here. `AppPortal.Server device add` writes `devices.json` into the data directory, `/app/data` in the container, which is a named volume. It holds device names, endpoint IDs and SHA-256 hashes of device tokens, never a plaintext token. The token is printed once when the device is added; keep it in the password manager.
