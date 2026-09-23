# M6-03: Signed releases

**Milestone:** 6 (0.9.0)
**Depends on:** M2-04, M2-06
**Unlocks:** M6-04

## Goal

Every file a release ships is signed with Authenticode through SignPath Foundation: `AppPortal.exe` and the project's own DLLs, `AppPortal.Agent.exe`, the MSI and `AppPortalSetup.exe`. The release gate installs and tests the same signed bytes that get published, and it fails if signing was switched on and any of those files is unsigned. From the first signed release onwards, the agent runs an update only when the MSI is signed by the same publisher that signed the agent already installed.

Signing stays off until the owner's SignPath Foundation application is approved and the secret and variable exist. While it is off, the workflow produces unsigned releases with the same files and names as today.

## Context

- The roadmap's "Next" section lists code signing because SmartScreen warns on every download, and the only thing an administrator can check a file against is `SHA256SUMS`. The owner has chosen SignPath Foundation. It offers free signing to open-source projects, and certificates are issued in the name "SignPath Foundation", not the project's author. The project is MIT-licensed and public.
- SignPath Foundation signs only what it can trace to the public repository. Its GitHub integration uses origin verification: the artifact must be uploaded by a workflow run in this repository, on a **GitHub-hosted runner**, from a ref the signing policy allows. It does not accept self-hosted runners. `installer-verify` runs on `${{ vars.WINDOWS_RUNNER || 'windows-latest' }}`, so a repository variable can move it to a self-hosted runner. That job builds the payloads, the MSI and the bootstrapper, tests them, and uploads the `AppPortal-msi` and `AppPortal-setup` artifacts that the `release` job publishes. Anything that gets signed therefore has to be built somewhere other than that job.
- Signing has to follow the build order, because each stage embeds the one before it:
  - The client is self-contained but **not** single-file. `out/client` holds `AppPortal.exe` (the apphost), `AppPortal.dll`, `AppPortal.Shared.dll` and more than a hundred runtime and Avalonia files.
  - The agent is a single-file bundle, `out/agent/AppPortal.Agent.exe`.
  - WiX harvests both folders into the MSI, so those files must be signed before `dotnet build` of `AppPortal.Installer.wixproj`.
  - `Protect-EnrollmentLogging.ps1` runs after the MSI build and rewrites the MSI's `CustomAction` table. The MSI can only be signed after that, and nothing may open it for writing afterwards. The installer README already says to keep this step before signing.
  - `AppPortalSetup.exe` is a single-file bundle that carries the MSI as a managed resource (`-p:AppPortalMsiPath=`). The MSI must be signed before the bootstrapper is built, and the bootstrapper is signed last. SignPath cannot open a .NET single-file bundle to sign what is inside it, so this is three signing requests in sequence; one deep-signing request cannot express it.
- Today the agent checks updates in `src/AppPortal.Agent/Update/UpdateDownloader.cs`: it downloads `AppPortal-<v>-x64.msi` and compares its hash with the `SHA256SUMS` from **the same release**. That catches corruption and a swapped file on the download path. It does not catch anyone who can change release assets (a leaked token with `contents: write`, a compromised workflow, or a `client.json` `updateRepository` pointed somewhere hostile): they can replace both files together, and `SelfUpdate.ApplyAsync` then runs that MSI as SYSTEM. An Authenticode check against a publisher the attacker cannot sign as closes that gap. It must not strand three kinds of PC:
  - 0.8.0 agents, which have no such check and simply run the first signed release.
  - PCs running a build their owner compiled unsigned.
  - PCs running a test-signed rehearsal build.
- Windows Installer does not refuse a tampered or unsigned MSI in a silent `msiexec /i` run. If the agent does not check, nothing does.
- The repository is public. Workflow comments and docs refer to "a self-hosted runner" only in general terms, never to a particular machine.

## Scope

### In

- **A hosted build job.** New job `build-windows` in `.github/workflows/ci.yml`, always `runs-on: windows-latest`. It takes over publishing the payloads, building the MSI, building the bootstrapper, running the release-feed `--check` step, and uploading `AppPortal-msi` and `AppPortal-setup`. When signing is on, it signs each stage in order through `signpath/github-action-submit-signing-request`.
- **`installer-verify` tests what `build-windows` built.** It keeps its runner choice (`WINDOWS_RUNNER`), needs `build-windows`, and downloads those two artifacts instead of building its own. It no longer uploads `AppPortal-*` artifacts. `release` publishes the same artifact files that `installer-verify` tested. One build path, whether signing is on or off.
- **The switch.** The `gate` job outputs `signing`: `off`, `test` or `release`. It is `release` only on a push of a `v*` tag in `Duresa7/app-portal` when the repository variable `SIGNPATH_ORGANIZATION_ID` is set. It is `test` only on a `workflow_dispatch` of `main` in that repository with the variable set and the new `sign` input ticked. Every other case is `off`: pull requests, forks, pushes to main, and dispatches of other branches. If the variable is set but the `SIGNPATH_API_TOKEN` secret is missing, a would-be signing run fails in the gate job with a readable error instead of quietly shipping unsigned.
- **Three artifact configurations**, committed under `.signpath/artifact-configurations/` as the source of truth: `payloads.xml`, `msi.xml`, `setup.xml`.
- **`deploy/windows/Test-Signatures.ps1`** (new). It checks the project's own PE files and the MSI in `off`, `test` or `release` mode, and fails a signing run on any unsigned or wrongly signed file.
- **`ci-installer-test.ps1` gains `-Signing`.** It checks the MSI and the bootstrapper before installing, and the installed `AppPortal*.exe`/`.dll` files in `%ProgramFiles%\App Portal` after installing. That proves the MSI carries the signed payload.
- **Signing never goes backwards.** On a tag, `installer-verify` fails if the previous release's MSI is signed by SignPath Foundation and this build is not, because every agent that release installed would refuse the update.
- **The agent checks the signature before it runs an update.** New `UpdateSignaturePolicy` with a WinVerifyTrust-based reader, wired into `SelfUpdate` between the download and the install or the staging. `--check` prints which rule applies to the running agent.
- **Docs:**
  - README: checking a download's signature, the SmartScreen note, and the code signing policy section SignPath Foundation requires.
  - README "Updates", "Releasing" and "Limits".
  - The installer README.
  - ADR 0002 for the agent's update trust rule.
  - The roadmap row, and the code signing bullet under "Next".
- **The owner's checklist** (below). It is done by hand and is not scripted.

### Out

- Signing third-party files (Avalonia, SkiaSharp, the .NET runtime). They ship as their authors published them.
- Checking the MSI's `UpgradeCode` or `ProductName` in the agent in addition to the publisher. See the risks: the publisher "SignPath Foundation" is shared by every project the foundation signs.
- Signing `SHA256SUMS` (minisign or GPG), signing the server image (cosign), EV certificates, and winget manifest submission.
- A GitHub environment around `SIGNPATH_API_TOKEN`, and SignPath user-defined parameters that pin the product version in the artifact configuration. Both are possible later hardening.
- The version bump to 0.9.0 and the tag. M6-04 does those.
- An override in `client.json` that lets a signed agent accept unsigned updates. Going back to unsigned is a one-time manual MSI deployment, and the docs say so.

## Interface

**Workflow (`.github/workflows/ci.yml`)**

- New `workflow_dispatch` input `sign`: boolean, default `true`, description "Test-sign the Windows build through SignPath (main only, when SignPath is set up)".
- New `gate` output `signing`: exactly one of `off`, `test`, `release`. Jobs read it through `env.SIGNING: ${{ needs.gate.outputs.signing || 'off' }}`. Signing steps use the positive form, `if: env.SIGNING == 'test' || env.SIGNING == 'release'`, so an empty or unexpected value means unsigned, never "sign".
- Jobs: `build-windows` (new, hosted), `installer-verify` (now `needs: [gate, version, build-windows]`), `release` (now `needs: [version, gate, test, build-windows, installer-verify, server]`).
- Published artifacts are unchanged: `AppPortal-msi` and `AppPortal-setup`, now uploaded by `build-windows`. Internal artifacts are `unsigned-payloads`, `unsigned-msi` and `unsigned-setup`, with `retention-days: 1`. They must never match the `AppPortal-*` pattern the release job downloads.
- Repository settings:
  - Variable `SIGNPATH_ORGANIZATION_ID`. Its presence is the switch.
  - Optional variable `SIGNPATH_PROJECT_SLUG`, default `app-portal`.
  - Secret `SIGNPATH_API_TOKEN`.
  - No credential value appears anywhere in the repository.
- SignPath names: project `app-portal`; signing policies `test-signing` and `release-signing`; artifact configurations `payloads`, `msi` and `setup`.

**`deploy/windows/Test-Signatures.ps1`**

```
Test-Signatures.ps1 -Path <file-or-directory>[,...] -Mode Off|Test|Release [-Publisher 'SignPath Foundation']
```

- The files it judges are the project's own: names matching `AppPortal*` with extension `.exe`, `.dll` or `.msi`, found directly in each directory given or passed as files. Other PE files in a directory given are listed with their signer, or "unsigned", and never fail the check.
- `Release`: `Get-AuthenticodeSignature` status `Valid`, a signer certificate whose simple name (`GetNameInfo('SimpleName', $false)`) equals `-Publisher`, and a non-null `TimeStamperCertificate`.
- `Test`: a signer certificate is present, and the status is neither `NotSigned` nor `HashMismatch`. A test certificate chains to an untrusted root, so `Valid` is not expected.
- `Off`: prints one line per file and fails nothing.
- It exits non-zero with a list of every file that fails, not just the first.

**`deploy/windows/ci-installer-test.ps1`**: new parameter `[ValidateSet('Off','Test','Release')] [string] $Signing = 'Off'`. `Off` keeps the script usable by hand on any machine.

**Agent (`src/AppPortal.Agent/Update/`)**

```csharp
public enum SignatureState { Unsigned, Valid, Invalid, Unsupported }

// Publisher is the signer certificate's CN, Organization its O; both null unless a certificate was read.
public sealed record FileSignature(SignatureState State, string? Publisher, string? Organization, string? Detail);

public interface IFileSignatureReader { FileSignature Read(string path); }

public sealed class WindowsFileSignatureReader : IFileSignatureReader;   // Unsupported off Windows

public sealed class UpdateSignaturePolicy(IFileSignatureReader reader, string runningAgentPath)
{
    public string? Refusal(string msiPath);   // null = may run; otherwise the sentence update.json carries
    public string Describe();                 // one line for --check
    internal static string? Decide(FileSignature running, FileSignature candidate, string msiName);
}
```

- `SelfUpdate` gains a required constructor parameter `UpdateSignaturePolicy signatures`, placed after `paths`.
- `update.json` keeps its shape. A refusal is `Result = Failed` with the refusal as `Message` and a null `StagedVersion`.

**The rule** (`Decide`):

1. The running agent's signature is anything other than `Valid` (unsigned, test-signed, unreadable, or not Windows): accept. The MSI has already passed its SHA-256 check. This keeps 0.8.0 → first signed release, self-built fleets and rehearsal installs working.
2. The running agent is `Valid` and the candidate is not `Valid`: refuse.
3. Both are `Valid` and the candidate's `Publisher` or `Organization` differs from the running agent's (ordinal, ignoring case): refuse.
4. Otherwise accept.

The publisher is never hard-coded in the agent. It is whatever signed the running agent, so a self-hoster who signs with their own certificate gets the same protection.

Refusal texts, which the tests assert:

- `"{msi} is not signed, and the App Portal on this PC is signed by {publisher}. It was not installed."`
- `"{msi} carries a signature that does not verify ({detail}). It was not installed."`
- `"{msi} is signed by {candidate publisher}, not by {publisher}, who signed the App Portal on this PC. It was not installed."`

`Describe()` returns either `"This agent is signed by {publisher} and installs only updates signed by the same publisher."` or `"This agent is not signed, so updates are checked by their SHA-256 alone."`

## Steps

1. **`Test-Signatures.ps1`.** Write the script to the interface above in the style of the other scripts in `deploy/windows` (`$ErrorActionPreference = 'Stop'`, `Set-StrictMode -Version Latest`, readable `throw` text). `Get-AuthenticodeSignature` reads MSI signatures as well as PE signatures. Try it by hand against an unsigned `out/` tree (`-Mode Release` must fail and name every own file) and against a Microsoft-signed DLL copied under an `AppPortal`-prefixed name (passes `-Mode Test`; fails `-Mode Release` on the publisher).

2. **The signature reader.** Add `src/AppPortal.Agent/Update/FileSignature.cs` with the enum, the record, the interface and `WindowsFileSignatureReader`.
   - Call `WinVerifyTrust` (wintrust.dll) with `WINTRUST_ACTION_GENERIC_VERIFY_V2`, `WTD_UI_NONE`, `WTD_CHOICE_FILE` and `WTD_STATEACTION_VERIFY`, then `WTD_STATEACTION_CLOSE`, with `fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN`.
   - Map the results:
     - `0` → `Valid`
     - `TRUST_E_NOSIGNATURE` (0x800B0100), `TRUST_E_SUBJECT_FORM_UNKNOWN` (0x800B0003) and `TRUST_E_PROVIDER_UNKNOWN` (0x800B0001) → `Unsigned`
     - `CRYPT_E_REVOKED` (0x80092010) → `Invalid` "the signing certificate was revoked"
     - `TRUST_E_BAD_DIGEST` (0x80096010) → `Invalid` "the file does not match its signature"
     - anything else → `Invalid` with the hex code
   - Revocation soft-fails the way Windows itself does. If the first call answers `CRYPT_E_REVOCATION_OFFLINE` (0x80092013) or `CERT_E_REVOCATION_FAILURE` (0x800B010E), repeat with `WTD_REVOKE_NONE`. If that verifies, return `Valid` with `Detail = "revocation could not be checked"`, and the policy logs it as a warning.
   - Read the signer from the verified state data (`WTHelperProvDataFromStateData`, `WTHelperGetProvSignerFromChain`, `WTHelperGetProvCertFromChain`, then `new X509Certificate2(pCertContext)`), and take `CN` and `O` from `SubjectName.EnumerateRelativeDistinguishedNames()`. `X509Certificate.CreateFromSignedFile` is acceptable only if it compiles without an obsoletion warning under .NET 10.
   - Mark the class `[SupportedOSPlatform("windows")]` behind an `OperatingSystem.IsWindows()` check that returns `Unsupported` elsewhere. The solution builds and tests on Linux.
   - Any exception becomes `Invalid` with the exception type in `Detail`.

3. **The policy.** Add `src/AppPortal.Agent/Update/UpdateSignaturePolicy.cs`. Read the running agent's signature once, lazily, and cache it: the file cannot change under a running service, because an upgrade restarts the service. Log at Information when rule 1 applies ("This agent is not signed, so {msi} is checked by its SHA-256 alone").

4. **Wire it in.**
   - In `SelfUpdate.CheckAsync`, directly after `downloads.FetchAsync` succeeds and before `Prune` and the `client.IsRunning()` check: call `signatures.Refusal(msi)`. If it returns text, log at Error and return `Failed(installed, latestText, refusal)`. A refused MSI must never be staged behind "Restart to update".
   - Leave the refused file where it is. The next pass then re-checks it without downloading 100 MB again on every PC every day, and `Prune` removes it once a newer release arrives. The `updates` folder is writable only by SYSTEM and Administrators, so the gap between the check and `msiexec` opening the file is not an opening for a user.
   - In `AgentRun`, construct `new UpdateSignaturePolicy(new WindowsFileSignatureReader(), Environment.ProcessPath ?? Path.Combine(updatePaths.InstallDir, "AppPortal.Agent.exe"))`.
   - In `UpdateCheck.RunAsync`, print `Describe()` after the feed lines. The exit code does not change.

5. **Agent tests** in `tests/AppPortal.Agent.Tests`:
   - `UpdateSignaturePolicyTests`: every row of the rule, including running `Invalid` (test-signed) accepting an unsigned candidate, running `Unsupported`, and a publisher that differs only in `O`.
   - In `SelfUpdateTests`, add a fake reader and pass it through the existing `Update(...)` helper. A refused candidate yields `Failed` with the refusal text, starts no `msiexec`, and leaves `StagedVersion` null even with the client open. An accepted one behaves exactly as today. Every existing test keeps passing unchanged apart from the helper.
   - `WindowsFileSignatureReaderTests`, which return early when `!OperatingSystem.IsWindows()`, as `AdminSessionTests` does:
     - `typeof(object).Assembly.Location` (System.Private.CoreLib, Authenticode-signed by Microsoft) reads `Valid` with publisher `Microsoft Corporation`.
     - A temp file of random bytes reads `Unsigned`.
     - A copy of CoreLib with one byte flipped in the middle reads `Invalid`.
   - These run in the gate's `windows-latest` test leg.

6. **`ci-installer-test.ps1`.** Add `-Signing`.
   - After "MSI static checks", run `Test-Signatures.ps1 -Mode $Signing -Path $msiPath` and, when `$Setup` is given, the setup path too.
   - After "The installed binaries report the release version", add a step "The installed files carry their signatures" that runs `Test-Signatures.ps1 -Mode $Signing -Path $installDir`.
   - Update the comment-based help.

7. **Artifact configurations.** Commit these three files verbatim, each with the header comment. Confirm the namespace and element names against SignPath's current reference before the owner pastes them in (see the facts to confirm).

   `.signpath/artifact-configurations/payloads.xml`

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <!-- Source of truth for the SignPath artifact configuration "payloads" in project app-portal.
        SignPath does not read this file: paste it into that configuration whenever it changes.
        The artifact is the GitHub artifact "unsigned-payloads", a zip rooted at out/. -->
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <zip-file>
       <pe-file path="client/AppPortal.exe"><authenticode-sign /></pe-file>
       <pe-file path="client/AppPortal.dll"><authenticode-sign /></pe-file>
       <pe-file path="client/AppPortal.Shared.dll"><authenticode-sign /></pe-file>
       <pe-file path="agent/AppPortal.Agent.exe"><authenticode-sign /></pe-file>
     </zip-file>
   </artifact-configuration>
   ```

   `.signpath/artifact-configurations/msi.xml`

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <!-- Source of truth for the SignPath artifact configuration "msi". Paste it into SignPath when it changes.
        The MSI is signed after Protect-EnrollmentLogging.ps1 has run and before the bootstrapper embeds it. -->
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <zip-file>
       <msi-file path="AppPortal-*-x64.msi"><authenticode-sign /></msi-file>
     </zip-file>
   </artifact-configuration>
   ```

   `.signpath/artifact-configurations/setup.xml`

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <!-- Source of truth for the SignPath artifact configuration "setup". Paste it into SignPath when it changes.
        Signed last: it carries the already signed MSI inside it. -->
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <zip-file>
       <pe-file path="AppPortalSetup.exe"><authenticode-sign /></pe-file>
     </zip-file>
   </artifact-configuration>
   ```

   Explicit paths match exactly one file each, so a missing file fails at SignPath instead of being skipped. A new `AppPortal.*.dll` in the client would go unsigned at SignPath, and `Test-Signatures.ps1 -Mode Release` catches it by name.

8. **`gate` job.** Add the `sign` input and extend the `decide` step:

   ```yaml
   outputs:
     windows: ${{ steps.decide.outputs.windows }}
     signing: ${{ steps.decide.outputs.signing }}
   steps:
     - id: decide
       env:
         SIGNPATH_ORGANIZATION_ID: ${{ vars.SIGNPATH_ORGANIZATION_ID }}
         # Secrets cannot appear in an if:, so only whether it exists is passed, never its value.
         HAS_SIGNPATH_TOKEN: ${{ secrets.SIGNPATH_API_TOKEN != '' }}
         SIGN_INPUT: ${{ inputs.sign }}
       run: |
         # (the existing windows decision, assigned to $windows and written to GITHUB_OUTPUT as today)
         signing=off
         if [ "$windows" = true ] && [ "$GITHUB_REPOSITORY" = Duresa7/app-portal ] && [ -n "$SIGNPATH_ORGANIZATION_ID" ]; then
           if [ "$GITHUB_EVENT_NAME" = push ] && [[ "$GITHUB_REF" == refs/tags/v* ]]; then
             signing=release
           elif [ "$GITHUB_EVENT_NAME" = workflow_dispatch ] && [ "$GITHUB_REF" = refs/heads/main ] && [ "$SIGN_INPUT" = true ]; then
             signing=test
           fi
           if [ "$signing" != off ] && [ "$HAS_SIGNPATH_TOKEN" != true ]; then
             echo "::error::SIGNPATH_ORGANIZATION_ID is set but the SIGNPATH_API_TOKEN secret is not. Add the secret, or delete the variable to build unsigned."
             exit 1
           fi
         fi
         echo "signing=$signing" >> "$GITHUB_OUTPUT"
         echo "Signing: $signing"
   ```

   A pull request (same repository or fork) never reaches `release` or `test`, because only `push` and `workflow_dispatch` qualify. A fork fails the repository test even if it copies the variable; a fork that wants its own signing edits that line.

9. **`build-windows` job.** Move the publish, MSI, bootstrapper and `--check` steps out of `installer-verify` without changing what they run, and add the signing stages between them:

   ```yaml
   build-windows:
     name: Build the Windows release files
     needs: [gate, version]
     if: needs.gate.outputs.windows == 'true'
     # GitHub-hosted whatever WINDOWS_RUNNER says. SignPath signs only what a GitHub-hosted runner built
     # from this repository, and building here in every run means the files installer-verify tests are
     # the files the release publishes, signed or not.
     runs-on: windows-latest
     timeout-minutes: 240
     permissions:
       contents: read
       actions: read        # lets SignPath fetch the artifacts it signs, through github.token
     env:
       VERSION: ${{ needs.version.outputs.version }}
       SIGNING: ${{ needs.gate.outputs.signing || 'off' }}
       SIGNPATH_POLICY: ${{ needs.gate.outputs.signing == 'release' && 'release-signing' || 'test-signing' }}
   ```

   Each stage is three steps: upload, submit, then put the signed files back and check them. All of them use `if: env.SIGNING == 'test' || env.SIGNING == 'release'`.

   - **Payloads**, after "Publish the MSI payloads":
     - `actions/upload-artifact@v7`, `id: payloads`, `name: unsigned-payloads`, paths `out/client/AppPortal*.exe`, `out/client/AppPortal*.dll` and `out/agent/AppPortal.Agent.exe`, `retention-days: 1`, `if-no-files-found: error`. Do not set `archive: false`: the configurations assume a zip rooted at `out/`.
     - `signpath/github-action-submit-signing-request@v1` with:
       - `api-token: ${{ secrets.SIGNPATH_API_TOKEN }}`
       - `organization-id: ${{ vars.SIGNPATH_ORGANIZATION_ID }}`
       - `project-slug: ${{ vars.SIGNPATH_PROJECT_SLUG || 'app-portal' }}`
       - `signing-policy-slug: ${{ env.SIGNPATH_POLICY }}`
       - `artifact-configuration-slug: payloads`
       - `github-artifact-id: ${{ steps.payloads.outputs.artifact-id }}`
       - `wait-for-completion: true`
       - `wait-for-completion-timeout-in-seconds: 3600` (a release request waits for a person to approve it)
       - `output-artifact-directory: signed/payloads`
     - pwsh: `Copy-Item signed/payloads/client/* out/client -Force`, then `Copy-Item signed/payloads/agent/AppPortal.Agent.exe out/agent -Force`, then `./deploy/windows/Test-Signatures.ps1 -Mode $env:SIGNING -Path out/client, out/agent`.
   - **MSI**, after "Build the MSI": the same three steps with `unsigned-msi` (path `out/installer/AppPortal-${{ env.VERSION }}-x64.msi`), `artifact-configuration-slug: msi` and `signed/msi`. Copy the signed MSI over the one in `out/installer` and check it.
   - **Setup**, after "Build the bootstrapper around the MSI", which now embeds the signed MSI unchanged: `unsigned-setup` (path `out/setup/AppPortalSetup.exe`), `setup`, `signed/setup`. Copy it back and check it.
   - **"Check every file the release carries"**, always run: `Test-Signatures.ps1 -Mode $env:SIGNING -Path out/client, out/agent, "out/installer/AppPortal-$env:VERSION-x64.msi", out/setup/AppPortalSetup.exe`. In `off` mode this only reports.
   - **"The release feed still parses"**, moved as it is from `installer-verify`.
   - **The two uploads** `AppPortal-msi` and `AppPortal-setup`, moved as they are.

10. **`installer-verify` job.**
    - Add `build-windows` to `needs` and set `env.SIGNING` as above.
    - Replace the three build steps with two `actions/download-artifact@v8` steps: `AppPortal-msi` to `out/installer` and `AppPortal-setup` to `out/setup`.
    - Keep checkout and setup-dotnet: `ci-installer-test.ps1` builds the server.
    - Keep "Leave nothing of an earlier run in place", "Check the package on its own" and "Fetch the previous release".
    - After fetching the previous release, add "Signing never goes backwards":

      ```powershell
      $previous = Get-ChildItem previous/*.msi -ErrorAction SilentlyContinue | Select-Object -First 1
      if ($previous) {
          $signature = Get-AuthenticodeSignature $previous.FullName
          $wasSigned = $signature.Status -eq 'Valid' -and
              $signature.SignerCertificate.GetNameInfo('SimpleName', $false) -eq 'SignPath Foundation'
          if ($wasSigned -and $env:SIGNING -ne 'release') {
              $text = "$($previous.Name) is signed and this build is not. Every agent that release installed refuses an unsigned update. Restore the SignPath variable and secret."
              if ($env:GITHUB_REF -like 'refs/tags/v*') { throw $text } else { "::warning::$text" }
          }
      }
      ```

    - Pass `Signing = $env:SIGNING` in the `ci-installer-test.ps1` argument table.
    - Remove the two `AppPortal-*` uploads and the `--check` step. Keep the failure-log upload.
    - Update the comment on `runs-on`: the job may run on a self-hosted runner because it builds nothing that ships.

11. **`release` job.** Add `gate` and `build-windows` to `needs`. The download, checksum and publish steps are unchanged. Append this to the release `body`:
    `${{ needs.gate.outputs.signing == 'release' && ' The MSI, AppPortalSetup.exe and the App Portal executables are signed by SignPath Foundation; the README says how to check.' || '' }}`
    Update the header comment of the workflow to describe the hosted build job and the signing switch.

12. **Prove the off path before merging.** The Windows jobs do not run on a pull request, and this package reshapes them, so start the workflow from the Actions tab on the `plan/M6-03` branch with *Run the Windows jobs as well* ticked. The branch is not main, so signing is `off` whatever else is configured. `build-windows`, `installer-verify` and the Windows test leg must be green, and the run's artifacts must be `AppPortal-msi` and `AppPortal-setup` with today's file names. Link the run in the pull request.

13. **Docs.**
    - README, new section **Checking a download**, after "Deploy the MSI":
      - `Get-FileHash -Algorithm SHA256` against `SHA256SUMS`.
      - `Get-AuthenticodeSignature .\AppPortalSetup.exe | Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate` and the same for the MSI. Expect `Valid` and a signer beginning `CN=SignPath Foundation`.
      - Explorer's Properties, Digital Signatures tab.
      - Why the publisher is SignPath Foundation and not the author, and what that signature attests: built by this repository's workflow on a GitHub-hosted runner from a `v*` tag, and approved by hand.
      - Releases before the first signed one carry no signature.
      - The SmartScreen note: a newly published file can still get a SmartScreen prompt until it has reputation, but the prompt names SignPath Foundation instead of "Unknown publisher".
    - README, new section **Code signing policy**, in the form SignPath Foundation requires:
      - "Free code signing provided by SignPath.io, certificate by SignPath Foundation", with links.
      - Committers and reviewers: the repository's maintainers. Approvers: the owner, by GitHub handle only, never an email address.
      - What is signed and how, in two sentences.
      - A privacy statement that is true for this product: App Portal sends data only to systems its administrator configures (the App Portal server named at install, GitHub's release feed for updates, and the package sources in the catalog), and nothing to the project's authors or to SignPath.
    - README "Updates": the signature rule, the fact that it starts with the first signed agent, and how to leave it (deploy an unsigned MSI once by hand, the same way a fleet was first deployed; after that, updates are checked by SHA-256 alone again). Say the same for a fleet pointed at a fork with `updateRepository`.
    - README "Releasing":
      - `build-windows` always builds on a GitHub-hosted runner, and `installer-verify` tests its files wherever it runs.
      - The switch and the `sign` rehearsal input.
      - On a tag with signing on, the owner approves three SignPath requests in order (payloads, MSI, setup), each within an hour. A timed-out run is re-run with "Re-run failed jobs", which submits fresh requests.
      - Update the `ci-installer-test.ps1` example to show `-Signing`.
    - README "Limits": replace "verified by SHA-256 but not signed" with the new rule and its one limit: the publisher SignPath Foundation is shared with every other project the foundation signs.
    - `src/AppPortal.Installer/README.md`: CI signs the payload before WiX harvests it and signs the MSI after `Protect-EnrollmentLogging.ps1`. Nothing may open the MSI for writing after that. `Read-MsiTable` opens it read-only.
    - `docs/adr/0002-a-signed-agent-takes-only-signed-updates.md`, in the format of 0001: the trust anchor is the running agent's own signature, the alternatives rejected (thumbprint pinning breaks at certificate renewal; a hard-coded publisher breaks self-hosters), and the shared-publisher limit.
    - `docs/ROADMAP.md`: update the M6-03 row as the rules say.

### Done by the owner, by hand

Credentials stay in the password manager and in GitHub secrets. None of the values goes into a file, a commit, an issue, a log or a chat message.

- [ ] Confirm the prerequisites SignPath Foundation states: the repository is public with an OSI licence (MIT); a release already exists; multi-factor authentication is on for every account with write access to the repository.
- [ ] Merge this package first, so the **Code signing policy** section the application points at is live on `main`.
- [ ] Apply through SignPath Foundation's application form for open-source projects, giving the repository URL. Wait for approval.
- [ ] In SignPath, find the organization the foundation set up and store its organization ID in the password manager.
- [ ] Create or confirm project `app-portal` with the repository URL. Link the predefined GitHub.com trusted build system to the organization and the project, and install the SignPath GitHub App on the repository if SignPath asks for it.
- [ ] Signing policies:
  - `release-signing`: origin verification on, allowed ref `refs/tags/v*` only, manual approval by the owner.
  - `test-signing`: origin verification on, allowed ref `refs/heads/main`.
  - If SignPath offers "require the ref to be protected", turn it on.
- [ ] Create artifact configurations `payloads`, `msi` and `setup`, pasting the three files from `.signpath/artifact-configurations/`.
- [ ] Create the CI user or API token with the submitter role on both policies. Store it in the password manager. Set it as the repository secret `SIGNPATH_API_TOKEN` straight from the password manager (for example `op read … | gh secret set SIGNPATH_API_TOKEN`), so the value is never typed or printed.
- [ ] Only if the project slug is not `app-portal`: add the repository variable `SIGNPATH_PROJECT_SLUG`.
- [ ] Last, because this is the switch: add the repository variable `SIGNPATH_ORGANIZATION_ID`.
- [ ] Rehearse: Actions, CI, Run workflow on `main`, with *Windows* and *sign* ticked. The run must be green and every own file test-signed.
- [ ] Tag the release as usual and approve the three requests as they arrive.
- [ ] Download the published MSI and `AppPortalSetup.exe`, and check both as the README describes.

To switch signing off again, delete `SIGNPATH_ORGANIZATION_ID`. Once a signed release is out, the next tag's `installer-verify` then refuses to ship an unsigned one, by design.

## Acceptance criteria

- With `SIGNPATH_ORGANIZATION_ID` unset:
  - A dispatch run and a tag run build on `build-windows` (GitHub-hosted) and test on `installer-verify` (the `WINDOWS_RUNNER` choice).
  - No SignPath step runs.
  - The release carries `AppPortal-<v>-x64.msi`, `AppPortalSetup.exe` and `SHA256SUMS` with today's names and contents, unsigned.
  - Setting or clearing `WINDOWS_RUNNER` changes where `installer-verify` runs and nothing else.
- A pull request and a push to `main` run no Windows job and never reach a SignPath step. The gate prints `Signing: off`.
- With the variable set and the secret missing, a tag run fails in `gate` with the message above, and nothing is published.
- With SignPath configured, a dispatch of `main` with *sign* ticked is green, and `Test-Signatures.ps1 -Mode Test` passes on every own file, both in `build-windows` and on the files installed by `ci-installer-test.ps1`.
- With SignPath configured, a tag run publishes files where:
  - `Get-AuthenticodeSignature` reports `Valid`, signer `CN=SignPath Foundation`, and a timestamp for `AppPortal-<v>-x64.msi`, `AppPortalSetup.exe`, and the installed `AppPortal.exe`, `AppPortal.dll`, `AppPortal.Shared.dll` and `AppPortal.Agent.exe`.
  - The SHA-256 of each published file equals the file `installer-verify` tested (same artifact).
- A tag run with signing on in which any own file comes back unsigned fails before `release`, naming the file.
- On a tag, if the previous release is signed and this build is not, `installer-verify` fails with the "Signing never goes backwards" message.
- The agent follows the rule: `UpdateSignaturePolicyTests` and the new `SelfUpdateTests` pass on Linux and Windows, and `WindowsFileSignatureReaderTests` pass on the Windows leg. An unsigned agent (every build before the first signed release) installs an unsigned or test-signed update exactly as today. A signed agent refuses an unsigned or differently signed one, with the refusal text in `update.json`, and never stages it.
- `AppPortal.Agent.exe --check` prints the signing line. In `build-windows` with signing on, the line reads "signed by SignPath Foundation".
- No credential value, organization ID or token appears in any committed file, workflow log line or document. No self-hosted machine is named.

## Verification

- `dotnet format --verify-no-changes`, `dotnet build -c Release`, `dotnet test -c Release` on Linux. The Windows test leg runs through the dispatch run in step 12.
- Step 12's dispatch run on the branch, linked in the pull request: the off path, split jobs, same artifacts.
- After the owner's checklist: the rehearsal dispatch on `main` (test-signed, green), then the milestone 6 release tag (release-signed, three approvals).
- After the first signed release, on a throwaway VM: install it; set `"updateRepository"` in `client.json` to a fork whose latest release is an unsigned higher version with a correct `SHA256SUMS`; leave `update.request` in `%ProgramData%\AppPortal`. `update.json` must read `Failed` with the "is not signed" text, and `msiexec` must not have run (no `update-<v>.log`). Then deploy an unsigned build by hand and confirm the next pass accepts the fork's release. Record the result in the roadmap's milestone 6 paragraph.

## Facts to confirm against SignPath's current documentation

1. The action is `signpath/github-action-submit-signing-request@v1` (or a newer major) with the inputs named above; the default completion timeout; whether `output-artifact-directory` receives the extracted files (the copy-back steps assume extracted).
2. Origin verification rejects self-hosted runners, and whether it checks only the uploading job or the whole run. The design is safe either way.
3. The `github.token` permissions SignPath needs to fetch the artifact (assumed `actions: read` plus `contents: read`), and whether the SignPath GitHub App must be installed.
4. The artifact configuration namespace and elements, whether a wildcard `path` matches exactly one file by default, and whether SignPath can read these files from the repository (assumed not).
5. That Foundation projects get `test-signing` and `release-signing` under those slugs, that signatures are timestamped, and whether the Foundation requires restrictions such as `product-name`.
6. The exact wording the Foundation requires in the code signing policy section.
7. Whether the Foundation's certificate carries SmartScreen reputation today. The docs promise only a named publisher.

## Risks

- **Three approvals per release.** The owner approves three requests a few minutes apart during every tag run, each within the timeout. A two-request variant (deep-signing the payload inside the MSI) is possible only if SignPath repacks MSIs correctly, including the `File` table sizes and the rewritten `CustomAction` table; this plan does not rely on that.
- **The publisher is shared.** "SignPath Foundation" signs many projects, so the agent's publisher check alone does not tell App Portal apart from another Foundation-signed MSI. An attacker would need release write access and a Foundation-signed malicious MSI. Checking the MSI's `UpgradeCode` is a follow-up.
- **Certificate subject changes.** If the Foundation's certificate subject changes its CN or O at renewal, signed agents refuse the next release until it is deployed by hand. Thumbprint pinning was rejected because it breaks at every renewal.
- **Revocation soft-fail.** When no CRL/OCSP endpoint can be reached, revocation soft-fails, as UAC does. A strict proxy weakens revocation but does not stop updates.
- **The application itself.** The Foundation may question software that installs other software as SYSTEM. The application should explain that App Portal is admin tooling.
- **Build moves to hosted runners.** An artifact hop and a second Windows runner start are added to every pre-release run. For a public repository that costs nothing.

## Touches

`.github/workflows/ci.yml`, `.signpath/artifact-configurations/payloads.xml` (new), `.signpath/artifact-configurations/msi.xml` (new), `.signpath/artifact-configurations/setup.xml` (new), `deploy/windows/Test-Signatures.ps1` (new), `deploy/windows/ci-installer-test.ps1`, `src/AppPortal.Agent/Update/FileSignature.cs` (new), `src/AppPortal.Agent/Update/UpdateSignaturePolicy.cs` (new), `src/AppPortal.Agent/Update/SelfUpdate.cs`, `src/AppPortal.Agent/Update/UpdateCheck.cs`, `src/AppPortal.Agent/AgentRun.cs`, `tests/AppPortal.Agent.Tests/UpdateSignaturePolicyTests.cs` (new), `tests/AppPortal.Agent.Tests/WindowsFileSignatureReaderTests.cs` (new), `tests/AppPortal.Agent.Tests/SelfUpdateTests.cs`, `src/AppPortal.Installer/README.md`, `README.md`, `docs/adr/0002-a-signed-agent-takes-only-signed-updates.md` (new), `docs/ROADMAP.md`.
