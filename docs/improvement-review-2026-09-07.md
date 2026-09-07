# Improvement review — 2026-09-07

## Implementation follow-up

The findings below are the original assessment. The following changes have now been implemented:

- Health monitoring queues only the newest event for an independent publisher; slow IPC readers are disconnected after five seconds.
- Client request deadlines cover transmission and response handling, with connection reset after cancellation. Xray configuration validation has a 15-second deadline and child-process cleanup.
- A shared file-creation helper applies a protected SYSTEM/Administrators DACL atomically, before any content is written. Engine writes, rollback copies, and machine-settings sidecars use it. Existing engine backups are hardened and settings sidecars are unique and cleaned up on failure.
- Snapshot polling caches the non-secret settings projection. Prerequisites refresh every 30 seconds, with fresh checks before connection activation.
- Saving persists configuration before stopping the active route/guard. Activation errors and cancellation attempt to restore the requested guard and surface restoration failure.
- All four UI save actions share busy/error handling, prevent duplicate submissions, explain shared save scope and connection restart, and show pending changes. Scope labels and adapter instructions were corrected. Forms stack at narrow widths, account for text scaling, and show validation errors with focus/navigation to the affected field.
- Pull requests run read-only build/test validation. A Windows broker test project covers IPC, subprocess deadlines, cache behavior, guard activation, secret-file creation, and desktop save flows.

Validation completed:

- `dotnet build PaqetFire.slnx -c Release --no-restore -p:MSBuildEnableWorkloadResolver=false`: passed, zero warnings/errors.
- `dotnet test PaqetFire.slnx -c Release --no-restore --no-build -p:MSBuildEnableWorkloadResolver=false`: 67 passed, zero failed, two skipped.
- `git diff --check`: passed.
- A fresh independent security patch review found no concrete surviving bypass or regression. Actual file tests verified protected ACLs before the first byte and after writer close; an ordinary user could not reopen the secret file for reading.

Remaining verification: the two protected-file replacement/rollback and DPAPI round-trip tests require an elevated Administrator or SYSTEM token and were skipped locally. They are included for elevated Windows CI. Accordingly, the security patch is implemented but full privileged lifecycle verification remains **blocked by the local token**; it is not claimed fully verified. Live resize/screen-reader checks, installed-driver routing/leak tests, and performance profiling were not performed. Configuration files remain individually atomic, and changing ProxiFyre rules still requires a restart; this is not a machine-wide no-gap kill switch.

## Original assessment

Read-only assessment of the current application, with independent UI/UX, performance/reliability, and security reviewers. Application source was not modified.

## Security

The focused security scan reported one low-severity, medium-confidence finding: temporary engine configuration files are created with inherited permissions, written with plaintext secrets, and closed before restrictive permissions are applied (`src/PaqetFire.Broker/Configuration/AtomicConfigurationStore.cs:118`, `:168`, `:183`). Under ordinary readable parent permissions, another local user could race to read the temporary file before hardening. Actual installed ACLs and the race were not reproduced, so deployment permissions remain a qualification on exploitability.

Create temporary secret files with restrictive permissions from the start, or place them in a directory restricted to SYSTEM and Administrators before writing any secrets. Verify the permissions throughout creation, replacement, cancellation, and rollback.

The scan had partial coverage and was not an exhaustive security certification. Its generated report is at `C:/Users/Ali/AppData/Local/Temp/codex-security-scans-MDFnZj/PaqetFire/6537b720d7e22477b1bdabbd252619a778dae0e6_20260907T115752Z_10llf2pu/report.md`. Security scan token usage was unavailable.

## Recommended implementation order

1. Bound IPC writes and separate event delivery from health monitoring.
2. Bound Xray configuration validation and clean up its child process on timeout.
3. Fix routing save feedback, duplicate save submission, and the incorrect routing scope label.
4. Cover the broker and desktop boundaries with failure-oriented integration tests.
5. Reduce idle snapshot work and improve form navigation and narrow-window layouts.

## Reliability and performance

### P1: A non-reading IPC client can stall health recovery

`src/PaqetFire.Broker/BrokerWorker.cs:72` awaits publication in the health-monitor loop. `src/PaqetFire.Broker/Ipc/NamedPipeBrokerServer.cs:346` acquires a write lock and writes using the service lifetime cancellation token. Once a connected client's buffer fills, subsequent health checks and recovery can stop indefinitely.

Use bounded/coalescing event delivery independent of monitoring and disconnect clients after a write deadline. Verify with a client that connects but stops reading while an engine fails.

### P2: Xray validation has no operation deadline

`src/PaqetFire.Broker/Engines/XrayProcessAdapter.cs:212` starts `xray run -test` and awaits exit without a dedicated timeout. A stuck child holds up broker mutations and request handling. Disposing the Process wrapper does not terminate the child.

Add a validation deadline and guaranteed process termination/wait cleanup. Verify with a deliberately non-exiting test child.

### P2: The desktop request timeout starts after writing

`src/PaqetFire.Desktop/Ipc/NamedPipeBrokerClient.cs:151` writes the request before starting the response timeout. A blocked write can leave a UI operation busy beyond its advertised timeout.

Use one deadline for lock acquisition, request transmission, and response handling. Reset a connection after a partially transmitted frame times out.

### Optimization: Cache stable snapshot inputs

The three-second monitor rereads settings, decrypts secrets, and scans prerequisites through `PaqetFireRuntime.CreateSnapshotAsync` and `MachineSettingsStore.LoadAsync`. This represents approximately 28,800 iterations per continuously running day, excluding additional user requests.

Cache a non-secret settings view and prerequisite results with explicit invalidation after changes. Keep frequent engine health checks. Runtime CPU and disk costs were not benchmarked.

## UI and UX

### P2: Failed routing saves lack local error feedback

`src/PaqetFire.Desktop/MainWindow.xaml.cs:334` handles save success without a failure branch. A prior success notice may remain on the Routing page after a subsequent broker rejection.

Clear stale feedback before saving and show an explicit failure in RoutingInfoBar.

### P2: Selected-application routing is labeled Everything

`src/PaqetFire.Desktop/ViewModels/ConnectionViewModel.cs:580` always displays Everything while routing. Derive the label from the effective routing mode and selected-application count.

### P2: Repeated save clicks can queue connection restarts

Save buttons at `src/PaqetFire.Desktop/MainWindow.xaml:230` and `:371` lack busy disabling. The ViewModel serializes queued saves, and saving while connected restarts the route.

Disable duplicate submissions, show progress where saving began, and explain that applying changes restarts an active connection.

### P3: Adapter help requests an impossible edit

`src/PaqetFire.Desktop/MainWindow.xaml.cs:434` asks users to enter the router MAC manually, but the control at `MainWindow.xaml:205` is read-only. Provide instructions matching the actual broker-driven recovery workflow.

### Usability opportunities

- Show pending changes and clarify that either profile or routing save submits all configuration controls, including edits on another page.
- Add field-level validation and focus/navigation to the first invalid field; current errors are concatenated into a summary.
- Adapt multi-column forms for snapped windows and enlarged text. Verify with live accessibility and resize testing before choosing breakpoints.

## Verification and delivery

- Core tests passed: 42 passed, 0 failed, 0 skipped.
- Command: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj -c Release --no-restore -p:MSBuildEnableWorkloadResolver=false`.
- The workload resolver was disabled because `dotnet --info` encountered an installed SDK workload initialization exception.
- The sole test project references Core only. Add broker IPC, engine lifecycle, timeout, and desktop save-flow coverage, plus disposable-machine routing/leak tests.
- `.github/workflows/windows-build.yml:3` has push and manual triggers but no pull-request trigger. Add a PR validation job so changes are checked before merging; keep packaging/release actions separately scoped.
- Findings are based on source inspection. No live service failure injection, live UI testing, packet capture, or performance profiling was performed. The repository overview image was inspected as a visual reference, not as proof of current runtime behavior.
