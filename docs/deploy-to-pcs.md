# Installing App Portal on PCs

Every [release](https://github.com/Duresa7/app-portal/releases/latest) attaches two ways to put App Portal on a PC. Both need the server address and an enrollment key from **Enrollment keys** in the admin.

- `AppPortalSetup.exe`, a wizard for one PC at a time.
- `AppPortal-<version>-x64.msi`, for a fleet rollout through Group Policy, Intune or your RMM.

## One PC with Setup.exe

`AppPortalSetup.exe` is the whole product in one file: the MSI, the .NET runtime and a four-page wizard. Carry it to a machine, double-click it, answer two questions, and the PC is enrolled.

It asks for the server address and an enrollment key, checks both against the server before it installs anything, and asks for an Action1 endpoint id only when the key enrolls devices through Action1. It then runs the MSI, waits up to a minute for the agent to enroll and report in, and names the device as the server recorded it. Enter moves to the next page and Escape cancels, so the whole path works from the keyboard.

It requests elevation on launch, unpacks the MSI to `%TEMP%` and deletes it afterwards, and installs nothing of itself. The verbose Windows Installer log stays at `%TEMP%\AppPortal-Setup.log`, which is what the failure page points at.

Deployment systems that prefer an exe to an MSI can use silent mode from an elevated context:

```powershell
AppPortalSetup.exe /quiet /server https://portal.example.internal /key ape_... [/endpoint <endpoint-id>]
```

| Exit code | Meaning |
| --- | --- |
| 0 | Installed and enrolled |
| 1 | Enrollment failed: the key was refused, or the PC never checked in |
| 2 | The command line was wrong |
| 1620 | This build of `AppPortalSetup.exe` carries no MSI |
| 3010 | Installed, and the PC has to restart to finish |
| other | The code `msiexec` returned |

`/quiet` needs both `/server` and `/key`. Property values must not contain quotes, backslashes, tabs or line breaks, the same rule the MSI applies; setup refuses them before it installs rather than after.

## A fleet with the MSI

The MSI is what a fleet rollout uses; `AppPortalSetup.exe` above wraps this same package for one machine at a time. Run it elevated or as SYSTEM:

```powershell
msiexec /i AppPortal-<version>-x64.msi /qn SERVERURL=https://portal.example.internal ENROLLMENTKEY=ape_...
```

`ACTION1ENDPOINTID=<endpoint-id>` is optional. Pass the key through the deployment system's secret parameter. Property values must not contain quotes, backslashes, tabs or line breaks; percent-encode special characters in the URL.

The MSI installs the client and agent to `%ProgramFiles%\App Portal`, registers `AppPortalAgent` as an automatic SYSTEM service, and adds an all-users Start menu shortcut and an Apps & Features entry. It writes `%ProgramData%\AppPortal\enroll.json` only when both `SERVERURL` and `ENROLLMENTKEY` are supplied. Only SYSTEM and Administrators can read that file. On first start, the agent calls `POST /api/v1/enroll`, writes `client.json` with the returned device token, and deletes `enroll.json`. Users can read `client.json` but cannot change it. An existing token is preserved and any new enrollment file is discarded. Failed enrollment retains the key file and retries.

Upgrade silently with `msiexec /i AppPortal-<new-version>-x64.msi /qn`; no enrollment properties are needed. The token and local data survive, and the service restarts. Uninstall with `msiexec /x AppPortal-<version>-x64.msi /qn`. Data under `%ProgramData%\AppPortal` stays unless you also pass `REMOVEDATA=1`.

After the first install, the agent keeps the PC up to date by itself. See [Updates](how-it-works.md#updates).

## Moving off the old zip install

`AppPortal-client-win-x64.zip` is gone from 0.6.0 onwards, and the PowerShell installer scripts with it. A PC put on from one of those zips cannot reach a later release by itself: the updater it carries replaces files by renaming them, which is not how an MSI arrives. Move those machines once by deploying the MSI through whatever channel the zip went through. The MSI reuses the existing `client.json`, so the device keeps its token and does not enroll twice, and the agent clears what the zip left behind the first time it starts. From there the agent keeps the machine current on its own.

The **Agent** column on `/admin/devices` is how to find the machines that need this. Only the agent's enrollment and heartbeat write that column, so a device showing `—` has never run one and is still a zip installation. Those PCs go on working at the version they have and keep their place in the portal; they simply never move again, and they say nothing about it, so look rather than wait to notice.

## Checking a download

Every release lists the SHA-256 of its MSI and of `AppPortalSetup.exe` in `SHA256SUMS`. Compare a download with it before deploying:

```powershell
Get-FileHash -Algorithm SHA256 .\AppPortal-<version>-x64.msi
```

From the first signed release onwards, the MSI, `AppPortalSetup.exe` and the App Portal executables and libraries inside them carry an Authenticode signature too. Check it in PowerShell:

```powershell
Get-AuthenticodeSignature .\AppPortalSetup.exe | Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate
Get-AuthenticodeSignature .\AppPortal-<version>-x64.msi | Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate
```

Expect `Valid`, a signer that begins `CN=SignPath Foundation`, and a timestamp. In Explorer the same is under the file's **Properties**, **Digital Signatures** tab.

The publisher is SignPath Foundation, not the project's author, because the certificate is the foundation's: it signs open-source projects for free and holds the key. What its signature attests is that the file was built by this repository's workflow on a GitHub-hosted runner, from a `v*` tag, and that the owner approved the signing request by hand. See the [code signing policy](../README.md#code-signing-policy). Releases before the first signed one carry no signature, and `SHA256SUMS` is the only check they have.

A signature is not a SmartScreen pass. A newly published file can still get a SmartScreen prompt until it has built up reputation, but the prompt names SignPath Foundation instead of "Unknown publisher".
