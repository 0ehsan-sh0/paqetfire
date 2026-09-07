# PaqetFire

A native Windows control center for routing application and LAN-device traffic through Paqet transport with Xray destination policy.

## Language

**Profile**:
The saved server endpoint, transport key, routing choice, and sharing preferences for one connection.
_Avoid_: Config, settings blob

**Route**:
The path selected-application or LAN-client traffic takes through Xray policy to either Paqet transport or direct.
_Avoid_: Tunnel, VPN connection

**Paqet transport**:
The raw-packet/KCP carrier that forwards Xray-selected traffic to the Paqet server.
_Avoid_: VPN, proxy

**Xray policy**:
Destination rules for regional direct-routing, ad-blocking, QUIC and BitTorrent handling.
_Avoid_: Filter, firewall

**ProxiFyre capture**:
Transparent capture of selected Windows applications into the local Xray listener.
_Avoid_: VPN client, system proxy

**LAN share**:
An authenticated SOCKS5 listener on the active LAN IPv4 address for trusted devices on the same subnet.
_Avoid_: Proxy server, open proxy

**Hotspot network**:
The Windows mobile-hotspot Wi-Fi network hosted by this laptop for nearby devices.
_Avoid_: ICS, tethering, repeater, router

**Hotspot share**:
An authenticated SOCKS-only listener on the hotspot network address that applies the same route as LAN clients.
_Avoid_: Open proxy, repeater, ICS sharing, transparent NAT

**Hotspot client**:
A phone, tablet, or PC joined to the hotspot network that sends traffic to the hotspot share.
_Avoid_: Peer, user, guest
