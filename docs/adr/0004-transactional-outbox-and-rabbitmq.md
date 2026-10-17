# 4. Transactional outbox with RabbitMQ

Status: Accepted

## Context

Merchants and other services need to hear about payment changes. Publishing to a broker directly from the request has
two failure modes: the database commit succeeds but the publish fails (event lost), or the publish succeeds but the
commit fails (event for a change that never happened).

## Decision

- Domain events are converted to integration events and written to an `outbox_messages` table in the same transaction
  as the change (via the same in-transaction dispatcher that writes ledger entries).
- A background processor reads unpublished rows with `SELECT ... FOR UPDATE SKIP LOCKED`, publishes them to a RabbitMQ
  topic exchange with publisher confirms, and marks them published. Several instances can run it concurrently.
- If a publish fails, the processor stops the batch so later events for the same payment are not published first.
- The merchant webhook consumer acks only after it has stored the delivery; malformed messages are rejected to a
  dead-letter queue instead of being redelivered forever.
- Webhook deliveries have their own retry schedule (exponential backoff with jitter, capped at one hour) and move to
  `dead` after the maximum attempts. Merchants can list and replay dead deliveries.
- Without a configured broker, an in-process publisher is used so the service still runs locally.

## Consequences

- Delivery is at least once. A crash between publishing and marking a row republishes it, so consumers deduplicate:
  webhook deliveries have a unique index on `(event_id, url)` and every webhook carries a stable `Webhook-Id`.
- Events reach merchants a little after the request returns (about 100 ms to 1 s depending on the poll interval).
  The load test reports the publish lag.
- The outbox table grows; published rows need a retention job (not yet implemented).
