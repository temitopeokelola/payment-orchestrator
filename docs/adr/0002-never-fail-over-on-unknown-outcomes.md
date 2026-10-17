# 2. Never fail over on an unknown provider outcome

Status: Accepted

## Context

When a provider call fails we want to try another provider so the customer's payment still goes through. But not
every failure means the charge did not happen. A timeout or a 500 after the request was sent may hide a successful
charge. Retrying that payment on a second provider can charge the customer twice, which is worse than a failed
payment.

## Decision

Provider adapters classify every result into one of four outcomes:

| Outcome | Meaning | What the orchestrator does |
| --- | --- | --- |
| `Approved` | Provider accepted it | Authorize (and capture) the payment |
| `Declined` | Provider rejected it (card declined, insufficient funds) | Fail the payment; do not retry elsewhere |
| `Unavailable` | Request was refused before processing: connection refused, DNS failure, HTTP 429, open circuit | Try the next provider |
| `Unknown` | Request may have been processed: timeout, transport error after sending, HTTP 5xx | Stop. Leave the payment in `created` with the provider recorded |

A payment left in `created` is settled later by the provider's webhook (ADR 1's inbox dedupes it) or by reconciliation
(ADR 6), which reads the provider's own records. While the outcome is pending the payment cannot be cancelled.

Every provider call is made with an idempotency key derived from our payment id (`authorize-{id}` for Stripe, the
payment id as the Paystack reference), so retrying on the *same* provider is always safe.

## Consequences

- No double charges from failover, by construction.
- A small number of payments end the request in `created` rather than a final state; clients must handle that and
  listen for the `payment.captured` / `payment.failed` webhook.
- The classification lives in each adapter and is unit tested against recorded responses, since getting it wrong is
  the expensive mistake.
