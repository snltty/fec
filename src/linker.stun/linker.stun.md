# linker.stun

## Scope

- Implement a STUN client for public endpoint discovery and NAT behavior checks.
- Start with RFC 5389 Binding and RFC 5780 NAT Behavior Discovery.
- Keep the protocol codec separate from UDP transport so later standards can be added without rewriting the core.

## Initial Standards

- RFC 5389:
  - Binding Request / Success Response / Error Response.
  - `XOR-MAPPED-ADDRESS` first, `MAPPED-ADDRESS` compatibility fallback.
  - UDP transaction retry and timeout handling.
- RFC 5780:
  - `OTHER-ADDRESS`, `RESPONSE-ORIGIN`, and `CHANGE-REQUEST`.
  - Mapping behavior checks across primary address, alternate address with primary port, and alternate address with alternate port.
  - Filtering behavior checks with change-ip/change-port and change-port requests.
- P2P estimate:
  - `StunNatBehaviorResult.EstimatedP2PSuccessRate` is a heuristic 0-100 score for direct UDP hole punching.
  - IPv6/public no-NAT endpoints return 100.
  - Unknown or unsupported behavior returns unknown rather than inventing a score.
  - `StunNatBehaviorResult.P2PSummary` formats `MappingBehavior/FilteringBehavior/IPV4|IPV6|UNKNOWN-rate%`.

## Notes

- RFC 5389 alone can tell which public endpoint the server observes. It cannot reliably classify NAT mapping/filtering behavior.
- RFC 5780 behavior discovery requires server support for alternate IP/port response behavior. A normal RFC 5389 server can return a valid reflexive endpoint while still being unsupported for RFC 5780.
- Test targets: `stun.cloudflare.com`, `stun.snltty.com`, and RFC 5780-capable `stunserver2025.stunprotocol.org`.
