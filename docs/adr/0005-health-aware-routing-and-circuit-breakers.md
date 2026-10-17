# 5. Health-aware routing with per-provider circuit breakers

Status: Accepted

## Context

Providers differ in fees and supported currencies, and they fail. Routing purely by fee keeps sending traffic to a
cheap provider that is failing, adding its timeout to every payment before failover kicks in.

## Decision

- Each provider is wrapped in a Polly circuit breaker. `Unavailable` and `Unknown` results count as failures; declines
  do not, because a decline means the provider is working. When the breaker opens, calls fail fast as `Unavailable`
  without touching the provider, which is always safe to fail over from (ADR 2).
- A sliding-window health tracker records the outcome and latency of every call.
- The router keeps providers that support the currency (and separate capture, when the merchant only authorizes) and
  orders them by effective cost: fee in basis points plus a penalty proportional to the recent failure rate. A
  merchant's preferred provider goes first. Providers with an open circuit go last.
- Once a circuit's break period has elapsed the health tracker reports it as half-open, so the router ranks it normally
  again and the breaker gets its trial call. Without this, a router that always puts open circuits last never sends the
  call that closes them.
- Breaker thresholds can be tuned per provider, since a high-volume provider can tolerate a stricter breaker than a
  low-volume one.
- Every attempt is stored per payment (provider, operation, outcome, code, latency) and exposed through the API.

## Consequences

- During an outage, payments pay the failure cost of the broken provider only until its circuit opens; after that they
  go straight to the next provider. The k6 load test takes the primary provider down for 20 seconds at 100 payments
  per second and finishes with zero failed payments.
- After an outage, traffic stays on the backup until the failure window ages out, then returns to the cheaper provider.
- Health state is per instance. With several instances each learns independently, which is acceptable; sharing it
  (for example in Redis) is a possible later step.
