# Xray regional policy routing

PaqetFire bundles Xray-core 26.3.27 as a local destination-policy engine.
Xray is not a second VPN and does not replace Paqet.

The official latest-stable release and archive digest were rechecked on
2026-09-24; 26.3.27 remains current. See the
[engine release assessment](engine-update-2026-09-24.md) for feature opportunities
and upgrade validation.

## Data path

```text
selected Windows applications
  -> ProxiFyre
  -> Xray SOCKS5 on 127.0.0.1:1081
     -> direct outbound for enabled bypass/block rules
     -> Paqet SOCKS5 on 127.0.0.1:1080 for the catch-all
```

ProxiFyre always excludes the full installed paths of Paqet, Xray, ProxiFyre,
and the broker. Xray's direct and Paqet-bound sockets therefore cannot re-enter
the application redirector and form a loop.

Both loopback SOCKS5 hops require the broker's engine credentials, so other
local processes cannot open TCP connections through the route. Xray's UDP relay
does not authenticate individual datagrams; that is an upstream limitation.

## DNS

New profiles answer DNS inside Xray instead of the Windows resolver. The
resolver is selectable: Cloudflare (default), Google, Quad9, or System, which
keeps the previous behavior.

- Port 53 traffic from the ProxiFyre, LAN, and hotspot inbounds goes to a
  `dns` outbound. A and AAAA queries are answered by Xray's DNS module. Other
  query types (SRV, TXT, MX, HTTPS) are forwarded as plain DNS to the
  provider's resolver through Paqet, so they are never visible locally.
- The DNS module queries the provider over DNS-over-HTTPS at IP-literal
  endpoints, so no bootstrap lookup is needed, through the Paqet outbound.
- `geosite:private`, `geosite:category-ir` with the Iran preset, and custom
  direct domains use the active adapter's IPv4 DNS servers directly. These
  destinations already connect directly, and local answers keep Iranian CDN
  edges and LAN names working. Without a detected adapter resolver, they use
  DNS-over-HTTPS as well.
- Resolver rules come first, so user direct-IP or regional rules can never
  divert a DoH or domestic query.
- The direct outbound resolves through the same module (`UseIP`), and the query
  strategy drops unrouted address families: IPv4-only routing returns no AAAA
  records, so applications are not steered onto an uncaptured IPv6 path.

### FakeDNS (opt-in)

With FakeDNS on, the DNS module answers lookups that are not pinned to the
domestic resolver with placeholders from `198.18.0.0/15`, and every inbound
adds the `fakedns` sniffer. Connections to a placeholder reach Paqet as the
original domain, so the real lookup happens on the Paqet server and one tunnel
round trip per new domain is skipped. Xray's own lookups for routing and the
direct outbound never receive placeholders.

- Only IPv4 placeholders are issued. ProxiFyre's LAN bypass exempts
  `fc00::/7`, which contains Xray's default IPv6 pool; AAAA lookups therefore
  fall through to DNS-over-HTTPS.
- The broker flushes the Windows DNS cache on connect, disconnect, kill-switch
  guard, profile activation, and broker startup. A placeholder is meaningful
  only to the Xray process that issued it; transport recovery restarts Paqet
  alone for that reason.
- Validation rejects FakeDNS with the System resolver, without IPv4 routing,
  and with application exclusions in all-applications mode, where excluded
  applications share the Windows cache but bypass capture.

### DNS leak check

Verify route looks up `whoami.akamai.net`, whose answer is the address of the
resolver that asked, once through Xray addressed to the adapter's resolver
(as Windows would) and once directly from the broker. The same answer on both
paths means lookups leave through this network; a FakeDNS placeholder means
Xray answered locally.

Lookups outside the route are not covered: applications not selected in
selected-applications mode (Windows resolves for them in `svchost.exe`), and
queries to a LAN resolver while ProxiFyre's local network bypass is on.

## Default policy

- `geosite:private` and `geoip:private` go direct when LAN bypass is enabled.
- `geosite:category-ir` and `geoip:ir` go direct when the Iran preset is enabled.
- all remaining TCP and UDP traffic goes to Paqet.
- advertising blocking and direct BitTorrent are on for new profiles; UDP/443
  blocking is off. Measured through a Paqet route on 2026-10-19, HTTP/3 had
  slightly lower request latency than HTTP/2 (warm requests 113–117 ms against
  154–155 ms) but under half the download throughput (10–11 Mbit/s against
  17–23 Mbit/s), so enabling Block QUIC favors bulk transfers.
- `IPIfNonMatch` is the default domain strategy.

The UI can disable the regional preset with `None`. It also exposes the three
Xray domain strategies and optional ads, QUIC, and BitTorrent rules. Direct
BitTorrent explicitly warns that it exposes the machine's normal public IP.

## Lifecycle and integrity

The broker validates the generated JSON with `xray run -test` before launch.
Connect order is Paqet, Xray, then ProxiFyre. Disconnect and rollback use the
reverse order. Xray's executable and GeoIP/GeoSite databases are pinned in the
payload manifest and verified before a connection may start.

The generated `config.json` is broker-owned and written atomically under the
protected installation payload directory. It is not accepted from the UI.

## Live session statistics

Generated configurations enable Xray's `StatsService` on `127.0.0.1:10085` (loopback only; `HandlerService` is not exposed) with outbound uplink/downlink counters. The broker reads the `outbound>>>paqet>>>traffic>>>*` counters over gRPC for the Overview session card, so totals and rates include only traffic routed through Paqet, never direct or blocked traffic. Latency and the exit address are probed directly against the Paqet SOCKS5 listener, bypassing Xray so probe bytes are not counted. The exit country is resolved offline from the bundled `geoip.dat`; no address is sent to a geolocation service.
