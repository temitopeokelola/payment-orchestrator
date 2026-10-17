# payment-orchestrator

[![CI](https://github.com/temitopeokelola/payment-orchestrator/actions/workflows/ci.yml/badge.svg)](https://github.com/temitopeokelola/payment-orchestrator/actions/workflows/ci.yml)
[![Load test](https://github.com/temitopeokelola/payment-orchestrator/actions/workflows/load-test.yml/badge.svg)](https://github.com/temitopeokelola/payment-orchestrator/actions/workflows/load-test.yml)

A payment orchestration service in ASP.NET Core 10. It routes card payments across multiple providers (Stripe,
Paystack and built-in simulators), fails over when a provider goes down without ever risking a double charge, records
every money movement in a double-entry ledger, delivers signed webhooks to merchants through a transactional outbox and
RabbitMQ, and reconciles against each provider's own records.

## Highlights

- **Safe failover.** Provider results are classified as approved, declined, unavailable or unknown. Only requests
  that never reached the provider are retried elsewhere; timeouts are settled by webhook or reconciliation, never by
  charging a second provider. ([ADR 2](docs/adr/0002-never-fail-over-on-unknown-outcomes.md))
- **Health-aware routing with circuit breakers.** Providers are ranked by fee plus a penalty for recent failures; a
  Polly circuit breaker per provider stops traffic to a failing provider within milliseconds and probes it back in.
  ([ADR 5](docs/adr/0005-health-aware-routing-and-circuit-breakers.md))
- **Idempotent API.** Every money-moving request takes an `Idempotency-Key`; concurrent duplicates are resolved by a
  PostgreSQL insert-as-lock. ([ADR 1](docs/adr/0001-idempotency-keys-in-postgres.md))
- **Double-entry ledger enforced by the database.** Balanced, append-only journal entries posted in the same
  transaction as the payment change; Postgres triggers reject unbalanced or edited entries.
  ([ADR 3](docs/adr/0003-double-entry-ledger-enforced-by-the-database.md))
- **Transactional outbox, RabbitMQ and merchant webhooks.** At-least-once delivery with publisher confirms,
  `FOR UPDATE SKIP LOCKED` workers, HMAC-signed webhooks, exponential backoff, a dead-letter state and replay.
  ([ADR 4](docs/adr/0004-transactional-outbox-and-rabbitmq.md))
- **Reconciliation.** Compares provider settlement reports with our records, settles payments whose outcome was lost
  and flags money at risk. ([ADR 6](docs/adr/0006-reconciliation-against-provider-records.md))
- **Observability.** OpenTelemetry traces across HTTP, PostgreSQL, RabbitMQ and provider calls; business metrics in
  Prometheus; a provisioned Grafana dashboard; liveness and readiness checks.
- **Incident summaries.** An operator endpoint that turns provider health, failed calls and reconciliation differences
  into a plain-English summary, using Claude when an API key is configured and a rule-based summary otherwise.

## Load test

Measured by the [load test workflow](.github/workflows/load-test.yml) with [k6](loadtest/payments.js) on a GitHub
Actions `ubuntu-latest` runner, with the API, PostgreSQL and RabbitMQ running in Docker on the same machine. The test
sends a constant 100 payments per second for 60 seconds and **takes the primary provider down for 20 seconds** in the
middle. Each payment is persisted, routed, authorized and captured, posted to the ledger and published through the
outbox. The simulated providers add 40-60 ms of latency to every call.

| Metric | Result |
| --- | --- |
| Payments | 5,999 in 60 s (99.8/s) |
| Latency p50 / p95 / p99 | 75 ms / 109 ms / 335 ms |
| HTTP errors | 0.00% |
| Payments with an unexpected outcome | 0.00% |
| Payments routed to the backup provider | 4,006 |

The outage caused no failed payments: traffic moved to the backup provider when the primary's circuit opened, and
returned once the primary recovered and its failure window aged out.

## Architecture

```mermaid
flowchart LR
    Merchant -->|REST + Idempotency-Key| API[ASP.NET Core API]
    API --> Router{Health-aware router}
    Router -->|circuit breaker| P1[Stripe]
    Router -->|circuit breaker| P2[Paystack]
    Router -->|circuit breaker| P3[Simulators]
    API --> DB[(PostgreSQL<br/>payments, ledger, outbox,<br/>idempotency, attempts)]
    P1 & P2 & P3 -.->|signed webhooks| API
    DB --> Outbox[Outbox processor]
    Outbox -->|publisher confirms| MQ[(RabbitMQ)]
    MQ --> Consumer[Webhook consumer]
    Consumer --> DB
    DB --> Delivery[Delivery worker<br/>retries, dead letters]
    Delivery -->|HMAC-signed webhooks| Merchant
    Recon[Reconciliation job] --> DB
    Recon -->|settlement reports| P3
```

```
src/
  PaymentOrchestrator.Domain          Payment aggregate, Money, journal entries. No dependencies.
  PaymentOrchestrator.Application     Use cases, routing, provider ports, reconciliation, incident summaries.
  PaymentOrchestrator.Infrastructure  EF Core + PostgreSQL, provider adapters, circuit breakers, outbox, RabbitMQ, workers.
  PaymentOrchestrator.Api             Minimal API endpoints, API key auth, OpenTelemetry, health checks.
tests/
  PaymentOrchestrator.UnitTests         Domain rules, routing, failover, adapters (HTTP stubbed), reconciliation.
  PaymentOrchestrator.IntegrationTests  Full HTTP pipeline against real PostgreSQL and RabbitMQ (Testcontainers).
```

### Creating a payment

```mermaid
sequenceDiagram
    participant M as Merchant
    participant A as API
    participant DB as PostgreSQL
    participant R as Router
    participant P as Provider
    M->>A: POST /v1/payments (Idempotency-Key)
    A->>DB: claim idempotency key (INSERT ... ON CONFLICT)
    A->>DB: save payment (created)
    A->>R: rank providers (currency, capture mode, fee, health)
    loop until approved, declined or unknown
        R->>P: authorize (provider idempotency key)
        P-->>R: approved / declined / unavailable / unknown
    end
    A->>DB: payment + ledger entry + outbox messages (one transaction)
    A->>DB: store response for idempotent replay
    A-->>M: 201 payment
```

### Payment lifecycle

```mermaid
stateDiagram-v2
    [*] --> created
    created --> authorized: authorize
    created --> failed: decline / no provider
    created --> cancelled: cancel
    authorized --> captured: capture
    authorized --> cancelled: cancel
    captured --> partially_refunded: partial refund
    captured --> refunded: full refund
    partially_refunded --> refunded: refund the rest
```

A payment whose provider timed out stays in `created` until a webhook or reconciliation settles it, and cannot be
cancelled in the meantime.

## Running locally

Requirements: Docker. For development without Docker Compose, the .NET 10 SDK.

```bash
docker compose up --build
```

| Service | URL |
| --- | --- |
| API reference (Scalar) | http://localhost:8080/scalar |
| Grafana dashboard | http://localhost:3000 |
| Jaeger traces | http://localhost:16686 |
| Prometheus | http://localhost:9090 |
| RabbitMQ management | http://localhost:15672 (guest/guest) |

Development credentials:

| Role | API key |
| --- | --- |
| Merchant (`merchant_demo`) | `sk_test_demo_merchant_key` |
| Operator | `sk_ops_demo_operator_key` |

Only SHA-256 hashes of keys are stored in configuration.

### Try it

```bash
curl -X POST http://localhost:8080/v1/payments \
  -H "Authorization: Bearer sk_test_demo_merchant_key" \
  -H "Idempotency-Key: order-1001" \
  -H "Content-Type: application/json" \
  -d '{"amount": 500000, "currency": "NGN", "description": "Order #1001", "paymentMethod": "sim_card_approved", "customerEmail": "ada@example.com"}'
```

Send it again and you get the same payment back with `Idempotent-Replayed: true`. Then:

```bash
# Ledger entries for the payment, and the merchant's balance
curl http://localhost:8080/v1/payments/{id}/ledger -H "Authorization: Bearer sk_test_demo_merchant_key"
curl http://localhost:8080/v1/balance -H "Authorization: Bearer sk_test_demo_merchant_key"

# Take the primary simulator down and watch payments fail over (and its circuit open in Grafana)
curl -X POST http://localhost:8080/dev/simulators/simulator-a/outage -H "Content-Type: application/json" -d '{"down": true}'

# Webhooks the demo merchant received, with signature verification results
curl http://localhost:8080/dev/webhook-receiver
```

### Simulator test cards

| `paymentMethod` | Behaviour |
| --- | --- |
| `sim_card_approved` | Approved |
| `sim_card_declined` | Declined (`card_declined`) |
| `sim_card_insufficient_funds` | Declined (`insufficient_funds`) |
| `sim_card_timeout` | Charge succeeds but the response is lost (unknown outcome) |
| `sim_card_unavailable` | Refused before processing (safe to fail over) |

### Real providers

Stripe and Paystack adapters are enabled when their keys are configured:

```bash
Providers__Stripe__SecretKey=sk_test_...
Providers__Stripe__WebhookSecret=whsec_...
Providers__Paystack__SecretKey=sk_test_...
```

Stripe takes PaymentMethod ids (for example `pm_card_visa` in test mode) and supports separate capture. Paystack charges
saved authorization codes (`AUTH_...`) and captures immediately, so it is only routed payments that capture at once.

### Incident summaries with Claude

```bash
IncidentSummaries__AnthropicApiKey=sk-ant-...
curl "http://localhost:8080/v1/admin/incidents/summary?minutes=15" -H "Authorization: Bearer sk_ops_demo_operator_key"
```

Without a key the endpoint returns a rule-based summary of the same data.

## API

| Method | Path | Description |
| --- | --- | --- |
| `POST` | `/v1/payments` | Create (authorize, and by default capture) a payment |
| `GET` | `/v1/payments/{id}` | Get a payment |
| `POST` | `/v1/payments/{id}/capture` | Capture an authorized payment |
| `POST` | `/v1/payments/{id}/cancel` | Cancel a created or authorized payment |
| `POST` | `/v1/payments/{id}/refunds` | Refund all or part of a captured payment |
| `GET` | `/v1/payments/{id}/attempts` | Provider calls made for the payment |
| `GET` | `/v1/payments/{id}/ledger` | Journal entries for the payment |
| `GET` | `/v1/balance` | Merchant balance per currency |
| `GET` | `/v1/providers` | Providers, fees, circuit state and recent success ratio |
| `GET` | `/v1/webhook-deliveries` | Webhook deliveries, filterable by status |
| `POST` | `/v1/webhook-deliveries/{id}/replay` | Replay a delivery |
| `POST` | `/webhooks/{provider}` | Inbound provider webhooks (signature verified) |
| `GET` | `/v1/admin/ledger/trial-balance` | Operator: all account balances; must net to zero |
| `POST` | `/v1/admin/reconciliation/runs` | Operator: reconcile a provider for a period |
| `GET` | `/v1/admin/incidents/summary` | Operator: incident summary |

Errors use RFC 9457 problem details with a machine-readable `code`.

## Tests

```bash
dotnet test
```

Integration tests start PostgreSQL and RabbitMQ with Testcontainers, so Docker must be running. They cover the
idempotency race, failover and circuit recovery, webhook settlement and deduplication, the ledger's database guarantees,
outbox delivery through RabbitMQ, webhook retries, dead letters and replay, and reconciliation.

## Design decisions

1. [Store idempotency keys in PostgreSQL](docs/adr/0001-idempotency-keys-in-postgres.md)
2. [Never fail over on an unknown provider outcome](docs/adr/0002-never-fail-over-on-unknown-outcomes.md)
3. [Double-entry ledger, enforced by the database](docs/adr/0003-double-entry-ledger-enforced-by-the-database.md)
4. [Transactional outbox with RabbitMQ](docs/adr/0004-transactional-outbox-and-rabbitmq.md)
5. [Health-aware routing with per-provider circuit breakers](docs/adr/0005-health-aware-routing-and-circuit-breakers.md)
6. [Reconcile against the provider's own records](docs/adr/0006-reconciliation-against-provider-records.md)

## Not yet done

- Settlement report sources for Stripe and Paystack (the simulators implement the interface today).
- Retention jobs for published outbox rows and expired idempotency keys.
- Cloud deployment.
