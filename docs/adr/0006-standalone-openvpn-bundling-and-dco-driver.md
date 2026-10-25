# ADR 0006: Standalone OpenVPN Bundling and ovpn-dco Driver Provisioning

## Status
Accepted

## Context
In previous iterations, PaqetFire relied on external OpenVPN installations (searching `C:\Program Files\OpenVPN\bin\openvpn.exe` or system `PATH`). If a user did not have the OpenVPN Community Client installed on Windows, Upstream OpenVPN could not be used and failed with `EngineState.NotInstalled`.

Additionally, OpenVPN 2.7 removed support for `wintun` in favor of `ovpn-dco` (OpenVPN Data Channel Offload v2.8.x), which performs in-kernel packet processing with significantly higher throughput and lower CPU overhead than userland TAP or Wintun.

To achieve 100% standalone, zero-prerequisite operation for end users, PaqetFire must package the OpenVPN binaries and automatically manage the driver lifecycle without requiring manual software installation or reboots.

## Decision

1. **Self-Contained Engine Packaging in Payload**:
   - Bundle `openvpn.exe` (v2.7.8 x64) and its dependent runtime libraries (`libcrypto-3-x64.dll`, `libssl-3-x64.dll`, `libpkcs11-helper-1.dll`) directly in `payload/engines/openvpn/x64/`.
   - The broker's `OpenVpnLocator.FindExecutable(payloadRoot)` will locate this bundled executable first before falling back to any system path.
   - Total payload overhead is ~7.5 MB (compressed to ~3 MB in MSI cabinets).

2. **Signed ovpn-dco Driver Packaging & Automated Provisioning**:
   - Include the signed `ovpn-dco` driver package (`ovpn-dco.inf`, `ovpn-dco.cat`, `ovpn-dco.sys`) inside the engine payload at `payload/engines/openvpn/x64/driver/`.
   - The privileged broker service (running as `NT AUTHORITY\SYSTEM`), or the WiX MSI installer during installation, ensures the driver package is staged and installed using `pnputil.exe /add-driver ovpn-dco.inf /install`.
   - OpenVPN 2.7 automatically binds to the instantiated `OpenVPN Data Channel Offload` interface with `dev-type tun` and `dev tun`.

3. **Anonymous Loopback Forwarding**:
   - PaqetFire continues using anonymous loopback proxying on `127.0.0.1:1080` when Upstream OpenVPN is active to ensure compatibility with OpenVPN's SOCKS client.

## Consequences

### Positive
- Zero external software dependencies: Users do not need to download or install OpenVPN GUI or separate tools.
- High performance: Kernel DCO data channel offload provides native line-speed crypto and packet processing without user-kernel context switching.
- Clean isolation: OpenVPN engine files and drivers are managed and cleaned up by PaqetFire.

### Negative / Trade-offs
- Installer size increases by ~3.5 MB (from ~87 MB to ~90.5 MB).
- First-time driver installation requires administrative privileges (which the WiX MSI and Broker service already possess).
