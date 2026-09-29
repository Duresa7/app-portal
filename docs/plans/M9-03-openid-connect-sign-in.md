# M9-03: Sign in with Entra ID or OpenID Connect

**Milestone:** 9 (0.12.0)
**Depends on:** M1-11, M4-02
**Unlocks:** M9-05

## Goal

Administrators can sign in with the company's identity provider, such as Microsoft Entra ID, Okta or Google, through OpenID Connect, in the web admin and in the Windows client. Access is decided by a group or an app role in the provider, so nobody has to be added to the portal first. Local accounts keep working, and they are still checked first.

## Context

- "OpenID Connect admin sign-in" is on the Deferred list, and the roadmap's **Identity** decision names it as a later add-on. A company that uses Intune (M9-02) signs in with Entra ID already.
- Admin accounts have a `source` (`local` or `directory`, M1-11). A directory account needs nothing added: `AdminStore.EnsureDirectory` creates its row at the first sign-in that the directory accepts, if it is in the configured group. OpenID Connect works the same way, with the source `oidc`.
- The web admin uses a cookie session backed by `AdminSessionStore`. The client signs in with a user name and password through `POST /api/v1/admin/session` and gets an `apa_` bearer token (M4-01). A password form cannot carry a sign-in that happens at the provider, so the client needs the browser.
- The standard way for a desktop app to use a browser sign-in is a loopback redirect (RFC 8252) with PKCE (RFC 7636): the app listens on `http://127.0.0.1:<port>/`, opens the system browser, and gets a one-time code back on that address.

## Scope

### In

- **Options** `OpenIdConnect`: `Enabled` (off by default), `Authority`, `ClientId`, `ClientSecret` (from the environment), `DisplayName` ("Sign in with Microsoft"), `AllowedGroups` (group ids from the `groups` claim), `AllowedRoles` (values from the `roles` claim), `UsernameClaim` (`preferred_username`). At least one of `AllowedGroups` and `AllowedRoles` must be set, or the server does not start, with a sentence that says why. A sign-in with neither a matching group nor a matching role is refused.
- **Web:** the sign-in page shows a button with `DisplayName` when it is on. The ASP.NET Core OpenID Connect handler (`Microsoft.AspNetCore.Authentication.OpenIdConnect`), code flow with PKCE, signs in to an intermediate cookie; the callback checks the claims, calls `AdminStore.EnsureExternal(username, "oidc")`, and starts an ordinary admin session. A disabled `oidc` account is refused.
- **Client:** a "Sign in with {DisplayName}" button on the client's admin sign-in form, shown when `GET /api/v1/admin/session/methods` says OpenID Connect is on.
  1. The client listens on `127.0.0.1` on a free port, and makes a PKCE verifier and a state value.
  2. It opens `<server>/admin/client-sign-in?port=<port>&state=<state>&challenge=<S256 challenge>` in the system browser.
  3. That page requires a signed-in web session (any method: local, directory or OpenID Connect), then makes a one-time code that is good for 60 seconds, bound to the challenge, and redirects to `http://127.0.0.1:<port>/?code=<code>&state=<state>`. It redirects only to `127.0.0.1`.
  4. The client checks the state, shows "You can close this tab" in the browser, and posts the code and the verifier to `POST /api/v1/admin/session/exchange`, which returns the same `SignInResponse` as the password sign-in.
- **Admins page:** `oidc` accounts are listed with their source; a password cannot be set on them; they can be disabled.
- **Docs:** `docs/server-setup.md`, an "OpenID Connect sign-in" section with Entra ID steps (app registration, redirect URI `/signin-oidc`, the `groups` or `roles` claim) and a note for other providers.

### Out

- Signing in to the client portal itself. People at a PC are identified by Windows (the **Identity** decision).
- Roles inside the portal. Every administrator can do everything, as now.
- SAML.

## Interface

```csharp
public sealed record AdminSignInMethods(bool Password, bool OpenIdConnect, string? OpenIdConnectName);
public sealed record AdminCodeExchange(string Code, string Verifier, string? DeviceName = null);
```

Routes: `GET /api/v1/admin/session/methods`, `POST /api/v1/admin/session/exchange`, `GET /admin/client-sign-in`.

## Steps

1. Tests first: options validation; the claims check (group, role, neither, disabled account); the one-time code (single use, 60 seconds, wrong verifier, wrong port refused, redirect only to `127.0.0.1`); the exchange endpoint; the client's loopback listener and state check over a fake browser launcher.
2. Server options, handler, callback, codes, endpoints.
3. Client flow.
4. Pages, docs, and a test against a local OpenID Connect provider stub in the server tests.

## Acceptance criteria

- With a test provider, a user in an allowed group signs in to the web admin and to the client, and one outside every allowed group and role is refused with a sentence that says so.
- With OpenID Connect off, nothing about it shows, and the sign-in pages behave as in 0.11.0.
- Format, build and all tests pass.

## Touches

`src/AppPortal.Server/Options/OpenIdConnectOptions.cs` (new), `src/AppPortal.Server/Admin/{AdminAuth,AdminStore,AdminSessionStore}.cs`, `src/AppPortal.Server/Admin/ClientSignInCodes.cs` (new), `src/AppPortal.Server/Admin/Api/AdminSessionEndpoints.cs`, `src/AppPortal.Server/Pages/Admin/{Login,ClientSignIn,Admins}.cshtml*`, `src/AppPortal.Server/Program.cs`, `src/AppPortal.Server/AppPortal.Server.csproj`, `src/AppPortal.Shared/AdminContracts.cs`, `src/AppPortal.Client/Services/{AdminApiClient,AdminSession,BrowserSignIn}.cs`, `src/AppPortal.Client/ViewModels/Admin/SignIn*.cs`, `src/AppPortal.Client/Views/Admin/SignInView.axaml`, `docs/server-setup.md`, tests.
