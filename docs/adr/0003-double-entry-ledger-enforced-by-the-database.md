# 3. Double-entry ledger, enforced by the database

Status: Accepted

## Context

Merchants need balances they can trust, finance needs to know fees and revenue, and every movement of money must be
auditable. Keeping running totals on the payment row (or a `balance` column per merchant) loses history and can drift
when two updates race.

## Decision

Money movements are recorded as immutable journal entries made of postings (debits positive, credits negative):

| Event | Postings |
| --- | --- |
| Capture | Dr provider clearing (amount minus provider fee), Dr processing fees, Cr merchant payable (amount minus platform fee), Cr platform revenue |
| Refund | Dr merchant payable, Cr provider clearing |

- Balances are always computed by summing postings; nothing stores a balance.
- Entries are written by a domain event subscriber inside the same `SaveChanges` as the payment change, so a payment
  can never be captured without its entry, or the reverse.
- Fee rates are copied into the entry at posting time; changing a provider's fee later does not rewrite history.
- The database enforces the rules as well as the code:
  - a deferred constraint trigger rejects any entry whose postings do not sum to zero at commit;
  - triggers reject `UPDATE` and `DELETE` on entries and postings. Corrections are new entries.

## Consequences

- A trial balance across all accounts nets to zero per currency at all times; there is an operator endpoint and an
  integration test that check it.
- Querying a balance is an aggregate over postings. With an index on `account` this is fine at portfolio scale; at
  real scale it would move to periodic balance snapshots plus postings since the snapshot.
- Fees on refunds are not returned, which matches how most providers behave. Changing that is a new posting rule,
  not a schema change.
