# ProxiFyre integration contract

This note records the production integration contract for PaqetFire. It was
validated against the official ProxiFyre documentation and source on 2026-09-06.

## Runtime model

PaqetFire must treat ProxiFyre as a separately managed engine process, not load
`socksify.dll` into the broker. The recommended installed topology is:

- `ProxiFyreService`, running as `LocalSystem`, manual start, with an SCM
  dependency on `NDISRD`;
- PaqetFire Broker, which writes a validated configuration and then controls
  `ProxiFyreService` through the Service Control Manager;
- `app-config.json` beside the installed `ProxiFyre.exe`; and
- program-scoped inbound TCP and UDP firewall rules for `ProxiFyre.exe`, because
  the redirect listeners use dynamically allocated ports.

The official installer uses that exact service name/account/dependency and does
not start the engine until a valid configuration exists. See the
[official installer contract](https://github.com/wiresock/proxifyre/blob/main/docs/installer.md#service)
and [firewall contract](https://github.com/wiresock/proxifyre/blob/main/docs/installer.md#firewall-rules).

The broker should write configuration atomically before starting or restarting
the service. ProxiFyre reads the configuration only at startup, so saving a
change without a restart does not apply it. The upstream configuration lives
beside the resolved executable and the upstream GUI preserves a `.bak` file;
see the [official configuration reference](https://github.com/wiresock/proxifyre/blob/main/docs/configuration.md).

## Configuration and route behavior

PaqetFire supports the complete ProxiFyre rule surface:

- ordered application rules (first match wins);
- catch-all routing represented by exactly `"appNames": [""]` and placed last;
- higher-priority process exclusions;
- TCP and/or UDP;
- IPv4 and/or IPv6 destinations;
- plain SOCKS5/TCP or SOCKS5-over-TLS;
- username/password authentication (both or neither, each at most 255 UTF-8
  bytes);
- TLS server name, normalized 64-hex-character SHA-256 certificate pin, and the
  explicit unsafe-certificate option;
- LAN bypass; and
- all five engine log levels.

The upstream SOCKS endpoint itself must resolve over IPv4. IPv6 *destinations*
can still be routed. Unsupported destination families are blocked rather than
allowed to leak directly. Fragmented IPv6 datagrams are a documented exception:
ProxiFyre passes them directly. These constraints are documented in the
[official protocol/address-family reference](https://github.com/wiresock/proxifyre/blob/main/docs/configuration.md#protocols-and-address-families).

LAN bypass covers the upstream-documented local IPv4 ranges, including RFC1918,
link-local/APIPA, and multicast. It is intentionally opt-in because enabling it
means those destinations are direct.

## Loop-safe catch-all routing

For “route everything,” PaqetFire emits a catch-all rule and locks the following
exclusions so the user cannot remove them:

1. the full installed path of `paqet.exe`;
2. the full installed path of `ProxiFyre.exe`; and
3. the full installed path of `PaqetFire.Broker.exe`.

Full paths are preferred. ProxiFyre's exclusion matcher is deliberately
permissive: a name-only exclusion is a substring match and can accidentally
bypass unrelated programs. The carrier tunnel process must be excluded or its
outer packets can be captured by the catch-all and recursively depend on the
tunnel itself. See the upstream
[application matching and exclusion semantics](https://github.com/wiresock/proxifyre/blob/main/docs/configuration.md#exclusions).

`NDISRD`, Npcap, `wpcap.dll`, and `Packet.dll` are drivers/libraries, not carrier
processes. They do not belong in `excludes`; excluding the owning Paqet process
is what prevents the route loop. If a future Paqet build launches a separate
network carrier helper, that helper's full executable path must be added to the
locked set.

## Required bundled payload

Do not copy only `ProxiFyre.exe`. Stage one complete architecture-matched
upstream release and keep all of its engine dependencies together. The current
official MSI file set includes `ProxiFyre.exe`, its `.config`,
`ProxiFyre.Configuration.dll`, `socksify.dll`, Newtonsoft.Json, NLog, Topshelf,
and `NLog.config`. The authoritative list is in the
[upstream MSI contents](https://github.com/wiresock/proxifyre/blob/main/docs/installer.md#msi-contents-and-behavior).

The loose local folder at `C:\Users\Ali\Desktop\Internet\ProxyFyre` is a signed
ProxiFyre 2.6.0 payload, but it does **not** contain the Windows Packet Filter
driver installer or the Visual C++ redistributable. It therefore cannot by
itself satisfy PaqetFire's one-installer requirement.

For x64, the official ProxiFyre deployment contract currently requires:

- Windows Packet Filter `3.6.2.1` x64 MSI (819,200 bytes, SHA-256
  `9c388c0b7f189f7fa98720bae2caecf7d64f30910838b80b438ecf8956b8502c`);
- Windows Packet Filter API major `3`, minor `0x0601` or later, below major `4`;
- Microsoft Visual C++ 2015-2022 x64 runtime `14.44.35211.0` or later; and
- .NET Framework 4.7.2 or later.

The driver identity, compatibility checks, upstream filename, size, and pinned
hash are maintained in the
[official Windows Packet Filter prerequisite section](https://github.com/wiresock/proxifyre/blob/main/docs/installer.md#windows-packet-filter-prerequisite).

An MSI must not install another MSI with a custom action. To provide one user
experience while preserving reliable servicing, PaqetFire should ship a WiX
Burn bootstrapper that chains the unchanged prerequisite installers followed by
the PaqetFire MSI. An offline bundle can embed those payloads only when their
redistribution terms permit it; otherwise the bootstrapper must acquire and
verify them from pinned official URLs.

## Licensing and release gate

ProxiFyre is AGPL-3.0. Any distributed ProxiFyre binary needs the corresponding
license notices and source-availability compliance. The project states this in
its [official README](https://github.com/wiresock/proxifyre#license) and ships the
[AGPL-3.0 license](https://github.com/wiresock/proxifyre/blob/main/LICENSE).

Windows Packet Filter is a separate dependency. NT KERNEL states that it is free
for personal, educational, and nonprofit use, while publishers embedding its
precompiled driver in a product are offered a binary license. “No revenue” does
not automatically settle every redistribution case. Before distributing
PaqetFire beyond personal testing, confirm the intended use against the
[publisher's licensing page](https://www.ntkernel.com/windows-packet-filter/licensing/).

## Broker wiring requirements

The composition root must:

1. verify the closed payload manifest and hashes before activating engines;
2. construct the routing policy with the installed full paths of Paqet,
   ProxiFyre, and the broker as locked exclusions;
3. call `RoutingPolicyCompiler.CreateLockedExclusions(...)`, pass the same list
   to `ProxiFyreJsonConfigurationWriter.Write(...)`, and commit the resulting
   JSON beside `ProxiFyre.exe` through the atomic protected store;
4. start Paqet and wait for its SOCKS5 listener before starting ProxiFyre;
5. stop ProxiFyre before Paqet; and
6. surface missing `NDISRD`, service-start failure, configuration validation,
   and Paqet-listener failure separately.

The service adapter now performs an explicit `NDISRD` registration preflight
and accepts the bundled engine version for status reporting. Installer wiring
must still register `ProxiFyreService`, its `NDISRD` dependency, and the two
program-scoped firewall rules.
