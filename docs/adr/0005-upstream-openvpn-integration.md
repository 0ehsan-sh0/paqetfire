# Upstream OpenVPN Integration with Bundled Wintun Engine

## Context

Users in restrictive network environments rely on PaqetFire's KCP/raw-packet transport to bypass DPI and reach intermediate relay servers (e.g. in Europe). However, datacenter exit IPs may be flagged or restricted by target services (e.g., AI tools, Google Antigravity, streaming, or specific regional resources). Users previously established multi-hop chains manually using third-party tools (Proxifier + standalone OpenVPN GUI) to route OpenVPN over Paqet's local SOCKS proxy to residential or commercial VPN providers (such as Proton VPN in Canada/Netherlands).

To provide a seamless, native experience without requiring third-party tools or pre-installed VPN clients, PaqetFire needs to supervise OpenVPN directly while maintaining Xray's regional policy (Iran-direct bypass, local LAN bypass, and ad blocking), allowing users to switch between direct Paqet exit and Upstream OpenVPN exit on demand.

## Decision

1. **Bundled OpenVPN & Wintun Driver**:
   PaqetFire bundles `openvpn.exe` and `wintun.dll` within its engine payload. OpenVPN runs with `--windows-driver wintun` and creates an independent, isolated network adapter (`PaqetFire-VPN`) on demand without requiring separate kernel driver installations or reboots.

2. **Loopback Chaining & Physical Binding for Iran-Direct Bypass**:
   - The broker launches OpenVPN with `--socks-proxy 127.0.0.1 1080` (or `1081`), channeling all VPN control and data packets securely through Paqet transport.
   - OpenVPN establishes default routes (`0.0.0.0/1` and `128.0.0.0/1`) via its Wintun adapter for foreign exit traffic.
   - To preserve Iran-direct bypass and avoid routing local domestic traffic through the overseas VPN tunnel, Xray binds its `direct` outbound sockets to the active physical adapter IP (`sendThrough: <active_ip>`), routing Iranian domains (`geosite:category-ir`) and IP ranges (`geoip:ir`) directly to the local ISP.

3. **Per-Profile Upstream OpenVPN Toggle & Verification**:
   - Profiles support an optional, toggleable `UpstreamOpenVpn` configuration containing the imported `.ovpn` content, server metadata, and DPAPI-encrypted credentials.
   - Users can toggle Upstream OpenVPN ON or OFF at any time:
     - When OFF: Normal 3-engine chain (`Paqet` -> `Xray` -> `ProxiFyre`) with direct Paqet VPS exit.
     - When ON: 4-engine chain (`Paqet` -> `Xray` -> `OpenVPN` -> `ProxiFyre`) with OpenVPN provider exit.
   - Exit verification resolves the public IP and country corresponding to the OpenVPN provider (e.g. ProtonVPN Canada), never attributing to Psiphon or unmanaged tunnels.

4. **Proton VPN Onboarding & Guided Setup**:
   - The desktop UI presents an "Upstream OpenVPN" card with a guided help banner linking to `https://protonvpn.com/support/openvpn-windows-setup`, noting that users need a free Proton account and can download free TCP configurations and credentials from their dashboard.
