# M3-08: Device requirements and preflight

**Milestone:** 3 (0.5.0)
**Depends on:** M3-01, M3-02
**Unlocks:** M3-06

## Goal

A catalog app can state what a device must have before it is offered, the agent reports what each device has, and a device that cannot take the software is told why instead of watching an install fail.

## Context

- Some software refuses to work rather than refuses to install. A kernel-mode anti-cheat driver needs Secure Boot and TPM 2.0 on Windows 11; the installer succeeds on a device without them and the software then does not start. An exit code of zero is not the same as a working install.
- Large software needs disk. M3-04 checks free space at download time, on the device, after the person has already asked and waited. The catalog page is the better place to say no.
- `requirements` on the package definition arrives with M3-01. Nothing gathers or checks it.

## Scope

### In
- Migration 012: `device_capabilities(device_id TEXT PRIMARY KEY REFERENCES devices(id), secure_boot INTEGER, tpm2 INTEGER, os_build INTEGER, architecture TEXT, free_disk_bytes INTEGER, seen_at TEXT NOT NULL)`. Every column except `device_id` and `seen_at` is nullable: an older agent reports less, and an unknown capability is not a failed one.
- The agent adds capabilities to the heartbeat body. Sources, each wrapped so that one failure does not lose the rest: Secure Boot from `HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled`; TPM from `Win32_Tpm` in `root\CIMV2\Security\MicrosoftTpm`, counting it present only when it is enabled and its spec version is 2.0; the build number from `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\CurrentBuildNumber`; the architecture from `RuntimeInformation.OSArchitecture`; free space from the system drive.
- `RequirementCheck.Evaluate(capabilities, requirements) -> IReadOnlyList<string>` in the server, returning one readable sentence per unmet requirement and an empty list when everything passes or nothing is known.
- `CatalogApp` gains `Unavailable: string[]`, the unmet reasons for the calling device. An app that fails a requirement is still listed, with the install button disabled and the reasons shown. Hiding it makes a self-service portal look broken; saying "This PC needs Secure Boot turned on" sends the person somewhere useful.
- `POST /api/v1/installs` refuses an app whose requirements the device does not meet with 409 and the same sentences, because an old client must not be able to start an install the catalog would have refused.
- Device detail page shows the capabilities and when they were last seen. Catalog edit page gains the requirement fields.

### Out
- Turning Secure Boot or TPM on. Remediation of any kind. Requirements that depend on installed software; that is M3-10.

## Interface

`device_capabilities` table. `AgentHeartbeatRequest` gains `Capabilities: DeviceCapabilities?`. `CatalogApp.Unavailable: string[]`. `RequirementCheck.Evaluate` above.

## Steps

1. `DeviceCapabilities` record, requirement evaluation, and a table-driven test over every combination of known, unknown and unmet.
2. Agent collection behind an interface, each source failing independently; a fake for tests and the real one guarded by `OperatingSystem.IsWindows()`.
3. Migration, heartbeat storage, device page.
4. Catalog filter, install refusal, client display.

## Acceptance criteria

- An app that needs TPM 2.0 shows as unavailable on a device reporting no TPM, with the reason, and installs normally on a device that reports one.
- An install of that app posted directly to the API from the device without a TPM is refused with 409 and the reason.
- A device whose agent predates this package reports nothing, and every app stays available to it.

## Verification

`dotnet test`; manual check on a VM with Secure Boot and a VM without.

## Touches

`src/AppPortal.Agent/Capabilities/**` (new), `src/AppPortal.Agent/HeartbeatWorker.cs`, `src/AppPortal.Server/Agent/AgentEndpoints.cs`, `Devices/DeviceStore.cs`, `Catalog/RequirementCheck.cs` (new), `Api/PortalEndpoints.cs`, `Pages/Admin/Devices/Detail.cshtml*`, `Pages/Admin/Catalog/Edit.cshtml*`, `Data/Migrations/012-device-capabilities.sql`, `src/AppPortal.Shared/Contracts.cs`, `src/AppPortal.Client/ViewModels/AppItemViewModel.cs`, `Views/*.axaml`, `tests/AppPortal.Server.Tests/RequirementCheckTests.cs`, `tests/AppPortal.Agent.Tests/CapabilityTests.cs`.
