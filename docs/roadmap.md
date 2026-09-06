# PaqetFire implementation roadmap

## P0 — Safe end-to-end connection

- Implement authenticated, ACL-restricted named-pipe IPC.
- Discover and verify the bundled engine payload from its signed hash manifest.
- Generate Paqet YAML and ProxiFyre JSON atomically with rollback.
- Start Paqet, wait for a healthy local TCP and UDP SOCKS5 endpoint, then start
  ProxiFyre. Stop them in the opposite order.
- Enforce locked Paqet, Xray, and ProxiFyre process exclusions in route-everything mode.
- Add a routing kill switch so application traffic covered by ProxiFyre cannot
  silently go direct when Paqet or Xray fails. (Completed in 0.5.)
- Detect recursive routing, port conflicts, driver failure, and stale services.

## P1 — Reliability

- Recover after Wi-Fi changes, DHCP renewals, sleep/resume, and adapter roaming.
- Re-detect local IP, interface GUID, gateway, and router MAC safely.
- Add bounded restart backoff with a visible degraded state.
- Monitor Paqet client/server version compatibility.
- Preserve the last known-good configuration and offer one-click rollback.
- Keep a crash-safe connection-state journal for interrupted upgrades/restarts.

## P1 — Leak and connectivity validation

- Verify TCP, UDP, DNS, IPv4, and IPv6 independently.
- Block unsupported protocols/address families instead of bypassing them.
- Test DNS behavior and expose the effective resolver path.
- Detect public-IP changes through the tunnel without logging sensitive data.
- Provide an opt-in LAN bypass with clear consequences; default it off for
  route-everything mode.

## P2 — User experience

- Guided first-run server setup with input validation and connection testing.
- Routing-mode choice: Everything or Selected applications.
- Installed/running application picker with icons and searchable executable list.
- Locked exclusions shown read-only with an explanation.
- Connection profiles, import/export with secrets removed, and profile switching.
- Notification-area controls, launch at sign-in, quiet reconnect notifications,
  accessible keyboard navigation, localization, and light/dark themes.

## P2 — Diagnostics and privacy

- Structured, bounded logs with automatic credential and key redaction.
- A copyable diagnostic bundle containing versions, hashes, driver state, ports,
  and recent sanitized events.
- CPU, memory, throughput, packet-loss, latency, and reconnect metrics.
- Explicit retention controls and a one-click local-data reset.

## P3 — Installer and supply chain

- Build a single architecture-matched setup executable with transactional repair,
  upgrade, and uninstall.
- Preserve profiles while removing services, drivers owned by the product, and
  firewall rules safely.
- Pin first-party release URLs and SHA-256 hashes; retain upstream notices.
- Sign PaqetFire executables, service, installer, and update metadata.
- Add rollback-protected updates and verify signatures before replacement.
- Decide Npcap OEM redistribution versus verified official acquisition.

## P3 — Verification matrix

- Automated tests for policy compilation and loop exclusions.
- Disposable-process integration tests for engine lifecycle and log parsing.
- Clean-machine installer tests on Windows 10/11, x64 and ARM64.
- Live TCP/UDP/DNS/IPv4/IPv6 routing tests with capture-based leak checks.
- Fault injection: engine crash, driver stop, port collision, corrupt config,
  interrupted update, network loss, sleep/resume, and forced shutdown.
