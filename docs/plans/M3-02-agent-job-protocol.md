# M3-02: Agent job protocol with progress

**Milestone:** 3 (0.5.0)
**Depends on:** M2-02
**Unlocks:** M3-03, M3-04, M3-05

## Goal

An install routed to the agent engine becomes a job the agent fetches, executes, and reports on, with download and install progress flowing back to the same install record the client already shows.

## Context

- `installs` rows carry `engine` and `external_ref`. For Action1, `InstallService` starts a deployment and `InstallStatusPoller` polls. For the agent, the server does not poll anything; the agent pushes progress.
- Client shows `PercentComplete` and `Detail` on the card and in Activity. Large downloads (games) need a visible percentage and a resumable download, which the executors implement; this package defines the state machine and transport.

## Scope

### In
- Migration 008: `agent_jobs(id TEXT PRIMARY KEY, install_id TEXT NOT NULL REFERENCES installs(id), device_id TEXT NOT NULL, definition_json TEXT NOT NULL, state TEXT NOT NULL, attempt INTEGER NOT NULL DEFAULT 0, leased_until TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)`. States: `queued`, `leased`, `downloading`, `installing`, `succeeded`, `failed`, `cancelled`.
- `IInstallEngine` interface in the server: `StartAsync(device, app, definition) -> external_ref`, `RefreshAsync(install)`; `Action1InstallEngine` wraps the existing code; `AgentInstallEngine` creates a job. `InstallService` picks the engine per M3-05 (until then: `agent` when the app has only an agent package and the device has the agent, otherwise `action1`).
- Agent endpoints, device bearer auth: `GET /api/v1/agent/jobs?wait=25` long-polls up to 25 seconds and leases the next queued job for 5 minutes; `POST /api/v1/agent/jobs/{id}/progress {state, percent, detail}` extends the lease and mirrors state, percent and detail onto the install row; `POST /api/v1/agent/jobs/{id}/complete {ok, detail, exitCode}` finishes it. A lease that expires requeues the job up to 3 attempts, then fails it.
- Agent side: a `JobRunner` loop with one job at a time, an `IPackageExecutor` interface keyed by definition `kind`, cancellation on service stop with the job returned to `queued`, and a stub executor that fails with "no executor" so the loop is testable before M3-03 and M3-04.
- Heartbeat interval drops to 60 seconds while jobs are queued for the device; server answers `heartbeatSeconds` accordingly.

### Out
- Actual winget or installer execution. Engine preference UI.

## Interface

Routes and bodies above. `IInstallEngine`, `IPackageExecutor` (`Task<ExecutionResult> RunAsync(PackageDefinition d, IProgress<(int percent, string detail)> p, CancellationToken ct)`). Install `Detail` strings during progress: "Downloading 43%", "Installing", "Verifying download".

## Steps

1. Migration, job store, engine interfaces, `InstallService` refactor; existing tests still pass with Action1.
2. Agent endpoints with lease semantics; tests for lease expiry and requeue.
3. Agent `JobRunner` with the stub executor; tests with a fake HTTP handler.
4. Smoke test: an app with only an agent package, a device with `has_agent`, a POST install yields a job visible through the agent endpoint using the device token.

## Acceptance criteria

- The client card shows "Downloading 43%" when the agent reports it, within one client refresh.
- Stopping the agent mid-job and restarting resumes the job (new lease) without a duplicate install row.

## Verification

`dotnet test`; smoke test; manual run of the agent in `--console` mode against the fake server.

## Touches

`src/AppPortal.Server/Installs/*` (engine interfaces, service), `Agent/AgentEndpoints.cs`, `Agent/AgentJobStore.cs` (new), `Data/Migrations/008-agent-jobs.sql`, `src/AppPortal.Agent/Jobs/**` (new), `src/AppPortal.Shared/Contracts.cs`, `deploy/smoke-test.sh`, `tests/AppPortal.Server.Tests/AgentJobsTests.cs`, `tests/AppPortal.Agent.Tests/JobRunnerTests.cs`.
