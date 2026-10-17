# 6. Reconcile against the provider's own records

Status: Accepted

## Context

Our database and the provider's books can disagree: a timed-out authorization that actually succeeded, a lost webhook,
a refund that failed at the provider after we recorded it, or a bug. Webhooks narrow the gap but are not guaranteed.

## Decision

A reconciliation run compares a provider's settlement report with our payments created in the same period, matching on
our payment id (which every adapter sends to the provider) and falling back to the provider reference. Each difference
becomes an item:

| Kind | Meaning | Handling |
| --- | --- | --- |
| `PendingSettled` | We had no outcome (timeout) but the provider charged | Settled automatically: payment authorized and captured, ledger entry posted |
| `ChargedButFailed` | We marked the payment failed but the customer was charged | Flagged: needs a refund |
| `AmountMismatch` | Captured or refunded amounts differ | Flagged |
| `MissingInternally` | Provider charge with no payment on our side | Flagged |
| `MissingAtProvider` | We recorded a capture the provider has no record of | Flagged |

- A background job reconciles every provider on a schedule, skipping the most recent minutes where webhooks may still be
  in flight. Operators can also trigger a run for any period.
- The settlement changes, their ledger entries and the run record commit in one transaction.
- Settlement reports are behind an interface. The simulators implement it from their own books; Stripe (balance
  transactions) and Paystack (transaction export) implementations are the next step.

## Consequences

- Payments with unknown outcomes always converge to the truth, even if the webhook never arrives.
- Only the safe direction is automated (recording money the provider already moved). Anything that would move money,
  such as refunding a `ChargedButFailed` payment, is left for a human.
