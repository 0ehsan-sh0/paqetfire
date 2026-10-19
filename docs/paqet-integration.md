# Paqet integration

## Pinned runtime

PaqetFire pins the Windows x64 Paqet payload to `v1.0.0-alpha.21`
(`4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4`). The release binary is 9,562,112
bytes with SHA-256:

`7D73F5130757B538C26C3ED76439A150978C3E06289C724DE37D916789AAF5DC`

The release artifact must be downloaded from the upstream release, not copied
from an existing machine. Release automation independently verifies the
published archive digest and the extracted executable's size and digest in
`payload/payload-manifest.json`.

Runtime invocation is:

```text
paqet_windows_amd64.exe run -c <broker>\payload\engines\paqet\x64\config.yaml
```

The broker owns that child process, captures bounded stdout/stderr logs, checks
the executable digest immediately before launch, refuses a SOCKS endpoint that
is already occupied, waits for a real SOCKS5 greeting on `127.0.0.1:1080`, and
terminates the whole process tree on disconnect. Paqet runs in a kill-on-close
job object, so it also ends if the broker crashes. Startup does not probe
UDP-associate; the on-demand connection verification report does.

## Configuration support

`PaqetProfile`, `PaqetConfigurationValidator`, and `PaqetYamlConfigurationWriter`
cover the client options of Paqet v1.0.0-alpha.21:

- SOCKS5 listen endpoint and optional username/password authentication;
- TCP/UDP port forwards;
- IPv4 and optional IPv6 capture details, TCP flag cycling, and PCAP buffer;
- connection count and IPv4 source port;
- KCP presets and manual controls, MTU/windows, write/ACK delay, all upstream
  encryption choices, SMUX buffers/keepalives, and optional FEC shards.

The Connection page exposes server, key, interface, KCP preset and flags.
Advanced transport settings additionally expose logging, IPv4 source port,
optional IPv6 capture details, PCAP buffering, connection count, and all KCP
tuning fields through `PaqetAdvancedOptions`. These settings survive save,
duplicate, switch, protected storage, and portable exports. Null numeric/boolean
values are deliberately omitted so the bundled engine supplies its defaults,
except the KCP windows: blank receive/send windows are written as 1024/512
packets. Paqet's client defaults of 512/128 cap a 150 ms route at roughly
37/9 Mbit/s, while its server sends with a 1024-packet window by default.
Logging defaults to `warn` because `info` records every SOCKS destination.
Existing profiles keep their saved log level. Manual controls are disabled when
a preset would override them. The app offers the documented manual ranges; its
keepalive inputs use positive seconds up to one day. Blank fields select
defaults instead of upstream's zero sentinel. Optional low-level port-forward
rules remain a writer capability, separate from packet tuning. The `transport.tcpbuf` and `transport.udpbuf` fields that appear
in older upstream examples do not exist in alpha.21 and are not written.

SOCKS listener wiring remains owned by the application. The listener requires
the broker's engine credentials, generated once and stored DPAPI-protected in
`%ProgramData%\PaqetFire\engine-credentials.json`; Xray's Paqet outbound, the
live-statistics probe, and ProxiFyre's link to Xray use the same credentials.
The readiness probe offers both no-auth and username/password so it only checks
that a SOCKS5 server answers.

A server host name is resolved by the broker before Paqet starts, with a direct
query to the adapter's resolvers from the broker's own socket, then the Windows
resolver, then the last address that worked. Paqet resolves a host name once at
startup through Windows, whose lookups ProxiFyre may be capturing into a route
that is not up yet, as during a transport restart or a kill-switch guard.

When updating the pinned release, re-audit the client schema in upstream
[`internal/conf`](https://github.com/hanselime/paqet/tree/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf)
rather than cached web examples, and align the validator's ranges with it.

## Tunnel watchdog

Paqet keeps running and retrying when its server stops answering, so process
state alone would report a dead route as connected. While connected, the broker
sends a HEAD request on a fresh SOCKS stream through Paqet every 10 seconds, to
`cp.cloudflare.com` and `connectivitycheck.gstatic.com` in parallel. After three
consecutive failures the snapshot reports the tunnel as stalled. The broker then
re-detects the network, rewrites the engine configurations, and restarts Paqet
only, with backoff doubling from 30 seconds to 5 minutes. Xray and ProxiFyre keep
running, so routed traffic fails closed rather than going direct, and FakeDNS
placeholders stay valid. One success resets the backoff.

## Licensing boundary

- Paqet is MIT licensed and can be redistributed when its copyright/license
  notice is included (`payload/licenses/Paqet-MIT.txt`).
- paqctl is AGPL-3.0. PaqetFire does not need to ship or invoke paqctl: the small
  Windows script's network discovery and configuration behavior is implemented
  independently in the broker. Copying paqctl code into PaqetFire would add
  AGPL source-distribution and notice obligations.
- Npcap is not open source and the free installer explicitly forbids
  redistribution. A public all-in-one installer may only bundle Npcap after an
  Npcap OEM redistribution license or written open-source redistribution grant
  is obtained. Without that grant, detect Npcap and direct the user to the
  official interactive download; do not put the free installer in the MSI.

The Npcap restriction applies even when PaqetFire is free and earns no revenue.
