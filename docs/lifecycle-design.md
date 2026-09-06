# Connection lifecycle design

`IConnectionController` is the broker-facing seam for bringing up and tearing
down the complete PaqetFire data path. Callers work with a single connection;
they do not coordinate the two engine adapters themselves.

## Invariants

1. Paqet starts first, Xray reaches `Running` second, and ProxiFyre starts last.
   This ensures the complete local SOCKS chain exists before application capture
   is enabled.
2. A normal disconnect stops ProxiFyre, Xray, and then Paqet. The optional
   routing kill switch instead leaves ProxiFyre running and stops Xray and Paqet,
   so covered applications stay attached to a deliberately unavailable proxy.
3. Only one connect or disconnect transition runs at a time. Contending callers
   wait on an internal gate and may cancel while waiting.
4. Connect and disconnect are idempotent for engines already in their requested
   stable state.
5. A connect failure rolls back only engines whose startup was attempted by that
   connect call. Rollback always stops ProxiFyre before Paqet and is deliberately
   not cancelled by the caller's token.
6. A successful adapter operation is not enough: the controller queries the
   adapter again and requires the corresponding stable engine state.
7. The controller owns lifecycle coordination, not adapter disposal. Its
   adapters are dependencies supplied by the broker and may be shared with
   monitoring or diagnostics services.

## Status model

`GetStatusAsync` returns both raw `EngineStatus` values plus a derived aggregate:

- `Connected`: Paqet, Xray, and ProxiFyre are running.
- `Disconnected`: all three engines are stopped.
- `Guarded`: Paqet and Xray are stopped while ProxiFyre remains running as the
  routing kill switch.
- `NotReady`: at least one engine is not installed.
- `Faulted`: at least one engine reports a fault.
- `Degraded`: the stable states disagree, such as Paqet running while
  ProxiFyre is stopped.
- `Connecting` or `Disconnecting`: a controller transition is in progress, or
  an adapter independently reports that transition.

Status reads do not take the transition gate. The desktop can therefore observe
progress while a long engine operation is running. Adapter implementations must
make `GetStatusAsync` safe to call while `StartAsync` or `StopAsync` is active.

## Failure and cancellation behavior

If Paqet startup fails, the controller attempts to stop Paqet because a failed
start may still have created a process or listener. If ProxiFyre startup fails,
the controller first stops ProxiFyre and then Paqet. Rollback failures are
retained in `ConnectionTransitionException.RollbackErrors`; they never replace
the original failure.

Cancellation before acquiring the transition gate has no side effects.
Cancellation during connect initiates the same uncancelled rollback as any other
startup failure. If rollback succeeds, the original `OperationCanceledException`
is preserved. If rollback also fails, a `ConnectionTransitionException` exposes
both the cancellation and cleanup failures.

Disconnect cancellation is honored between adapter operations. The controller
does not stop Paqet after ProxiFyre stop fails, because doing so could leave
capture enabled without its carrier. A Paqet shutdown failure leaves the safer
degraded state: capture is already disabled but the carrier may still be alive.

An adapter reporting `NotInstalled` prevents connection. Disconnect treats
`NotInstalled` like `Stopped`, which keeps cleanup and uninstall paths
idempotent. Engine processes changed outside the controller may yield a
`Degraded` snapshot. The broker supervisor normalizes that state to guarded or
fully disconnected, according to the saved kill-switch policy.

## Integration notes

Register the concrete Paqet, Xray, and ProxiFyre adapters with the broker, then create
one singleton `ConnectionController` over them. Named-pipe commands should
depend only on `IConnectionController`. The adapters should not return from
`StartAsync` until their own readiness checks complete; for Paqet this includes
the future TCP and UDP SOCKS probe. Adapter stop/start calls also need bounded
internal timeouts because rollback intentionally uses `CancellationToken.None`
to restore a safe state after caller cancellation.
