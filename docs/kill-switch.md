# Routing kill switch

PaqetFire 0.5 provides an optional application-routing kill switch. When it is
enabled, disconnecting or losing the Paqet/Xray chain does not stop ProxiFyre.
Protected applications remain redirected to Xray's unavailable local SOCKS
endpoint and their new connections fail instead of falling back to the normal
network path.

The broker restores this guarded state after a restart, before a protected
connection attempt, after a failed connection attempt, and whenever the health
monitor finds an unhealthy engine chain.

## Scope

This is deliberately called a **routing** kill switch. It covers applications
and traffic that ProxiFyre can attribute and that the selected routing policy
captures. It does not change the Windows Firewall default outbound policy and it
does not claim to block system or unattributed traffic that ProxiFyre cannot
redirect.

The setting is off by default. Turning it off and saving the routing policy
stops ProxiFyre when disconnected. Turning it on and saving arms the guarded
state immediately.

## Local network access

`Direct access — bypass PaqetFire` keeps private/LAN destinations outside the
route, which preserves access to routers, printers, NAS devices, and similar
local services. `Route through PaqetFire` sends those destinations into the
configured routing chain and can make local devices unreachable.
