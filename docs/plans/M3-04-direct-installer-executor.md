# M3-04: Direct installer executor

**Milestone:** 3 (0.5.0)
**Depends on:** M3-02
**Unlocks:** M3-06

## Goal

The agent downloads an installer from a URL, verifies its SHA-256, runs it silently as SYSTEM, and reports progress. This is the path for internal MSIs, vendors not in winget, and large games.

## Context

- Definition shape from M3-01: `url`, `sha256`, `installerType` (`msi`, `exe`, `msix`), `silentArgs`, `sizeBytes`, `uninstallKey`.
- Multi-gigabyte downloads must survive a network blip and a service restart: HTTP range requests resume a partial file; the hash is computed over the completed file.

## Scope

### In
- `DirectInstallerExecutor : IPackageExecutor` for `kind == "direct"`: download to `%ProgramData%\AppPortal\downloads\<sha256>.<ext>.part` with resume via `Range`, progress every 1 percent or 5 seconds, verify hash, rename to final, run: `msi` via `msiexec /i <file> /qn /norestart /l*v <log>`, `exe` via the file with `silentArgs`, `msix` via `Add-AppxProvisionedPackage` through PowerShell. Timeout 60 minutes for the run, configurable per definition later.
- Disk space check before download: refuse with a clear detail when free space is below `sizeBytes` plus 1 GB.
- Cache: keep the verified installer for 7 days so a second device on the same PC image or a retry does not re-download; evict oldest beyond 20 GB.
- Exit code mapping: 0 and 3010 (reboot required) succeed, with "Restart required" in the detail for 3010; 1641 succeed; others fail with the log tail.
- Installed-software reporting: after success, read the Uninstall registry key named in `uninstallKey` if given, otherwise scan for a new entry matching the app name, and post it to `/api/v1/agent/software` like the winget executor.

### Out
- Uninstall. Delta updates. Torrent-style peer distribution.

## Interface

None new beyond the folders and the reuse of `POST /api/v1/agent/software`.

## Steps

1. Resumable downloader with tests against a local Kestrel test server that drops the connection mid-stream and honours `Range`.
2. Hash verification and cache eviction tests.
3. Runners per installer type with a fake process runner; PowerShell path tested on a VM only.
4. VM test with a 3 GB dummy installer (an MSI that just sleeps) to see progress and resume after `Restart-Service`.

## Acceptance criteria

- A download interrupted by stopping the service resumes from the byte it stopped at, proven by the test server's request log.
- A wrong hash fails the job, deletes the file, and the detail says "Checksum mismatch".
- A 3010 exit shows as succeeded with "Restart required".

## Verification

`dotnet test`; VM run.

## Touches

`src/AppPortal.Agent/Executors/Direct*.cs` (new), `src/AppPortal.Agent/Downloads/*.cs` (new), `tests/AppPortal.Agent.Tests/ResumableDownloadTests.cs`, `tests/AppPortal.Agent.Tests/DirectInstallerExecutorTests.cs`.
