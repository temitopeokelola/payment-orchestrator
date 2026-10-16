# 1. Store idempotency keys in PostgreSQL

Status: Accepted

## Context

Clients retry payment requests when they time out or lose the connection. Without protection, a retry can create a
second payment and charge the customer twice. We need retries with the same `Idempotency-Key` to return the original
result, including when duplicates arrive at the same time.

Options considered:

1. **Redis `SET NX`** as a lock plus a cached response.
2. **PostgreSQL table** with a primary key on `(merchant_id, key)`.
3. **Unique column on the payments table** only.

## Decision

Use a PostgreSQL `idempotency_keys` table. A request claims a key with
`INSERT ... ON CONFLICT (merchant_id, key) DO NOTHING`. If one row was inserted, the request owns the key. Otherwise the
existing row decides the outcome: replay the stored response, report that the key is in progress, or reject a
different payload (compared by SHA-256 of method, path and body).

Keys are scoped per merchant so two merchants can use the same key value.

## Consequences

- The key and the payment live in the same database, so there is no window where Redis says "done" but the payment
  write was lost, or the reverse.
- One extra insert and update per request. This is acceptable at the expected volume and can move to Redis later if
  profiling shows it matters.
- A process crash after claiming but before completing leaves an in-progress row. Clients get `409` for that key until
  the row is cleaned up. A cleanup job for stale claims and expired keys is still to do.
- Option 3 was rejected because it only covers creation; capture and refund also need protection.
