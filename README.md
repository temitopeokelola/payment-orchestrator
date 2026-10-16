# payment-orchestrator

[![CI](https://github.com/temitopeokelola/payment-orchestrator/actions/workflows/ci.yml/badge.svg)](https://github.com/temitopeokelola/payment-orchestrator/actions/workflows/ci.yml)

A payment orchestration service built with ASP.NET Core and PostgreSQL. The goal is a production-style backend that routes
payments across multiple providers (Stripe, Paystack and others) with idempotent processing, a double-entry ledger,
reliable event delivery and reconciliation.

## Status

| Area | State |
| --- | --- |
| Payment lifecycle state machine | Done |
| Merchant API key authentication | Done |
| Idempotency keys on POST endpoints | Done |
| PostgreSQL persistence with optimistic concurrency | Done |
| Integration tests (Testcontainers) and CI | Done |
| Provider adapters (Stripe, Paystack, simulated provider) | Planned |
| Inbound provider webhooks with signature verification | Planned |
| Double-entry ledger | Planned |
| Transactional outbox and RabbitMQ | Planned |
| Outbound merchant webhooks with retries and DLQ | Planned |
| Routing engine with circuit breakers and failover | Planned |
| OpenTelemetry tracing, metrics and dashboards | Planned |
| Reconciliation job | Planned |
| Load testing with k6 | Planned |

## Architecture

```
src/
  PaymentOrchestrator.Domain          Payment aggregate, Money value object, domain events. No dependencies.
  PaymentOrchestrator.Application     Use cases and ports (repositories, idempotency store).
  PaymentOrchestrator.Infrastructure  EF Core + PostgreSQL implementations.
  PaymentOrchestrator.Api             Minimal API endpoints, authentication, error handling.
tests/
  PaymentOrchestrator.UnitTests         Domain rules.
  PaymentOrchestrator.IntegrationTests  Full HTTP pipeline against a real PostgreSQL container.
```

Dependencies point inwards: Api -> Infrastructure -> Application -> Domain.

### Payment lifecycle

```
            authorize            capture              refund (partial)
 created ------------> authorized ---------> captured -----------------> partially_refunded
    |                     |                     |                               |
    | fail                | cancel              | refund (full)                 | refund (rest)
    v                     v                     v                               v
  failed              cancelled              refunded <-------------------------+
    ^
    | cancel (from created)
```

Invalid transitions are rejected by the aggregate and returned as `409 Conflict`.

### Money

Amounts are stored as integers in the currency's minor unit (kobo, cents) together with an ISO 4217 code. Mixing
currencies throws, so a USD refund can never be applied to an NGN payment.

### Idempotency

Every money-moving `POST` accepts an `Idempotency-Key` header (required for creating payments and refunds).

- The first request claims the key with `INSERT ... ON CONFLICT DO NOTHING` on a `(merchant_id, key)` primary key, so
  only one concurrent request can win.
- The response is stored and replayed for retries with the same key, with an `Idempotent-Replayed: true` header.
- Reusing a key with a different body returns `422`. A retry while the first request is still running returns `409`.
- If processing throws or returns a 5xx, the claim is released so the client can retry safely.

See [docs/adr/0001-idempotency-keys-in-postgres.md](docs/adr/0001-idempotency-keys-in-postgres.md).

## Running locally

Requirements: .NET 10 SDK and Docker.

```bash
docker compose up --build
```

The API listens on `http://localhost:8080`. API reference (Scalar) is at `http://localhost:8080/scalar` in development.

Or run PostgreSQL in Docker and the API from your IDE:

```bash
docker compose up -d postgres
dotnet run --project src/PaymentOrchestrator.Api
```

Migrations are applied automatically on startup in the Development environment.

### Try it

The development config includes a demo merchant with the key `sk_test_demo_merchant_key`.

```bash
curl -X POST http://localhost:8080/v1/payments \
  -H "Authorization: Bearer sk_test_demo_merchant_key" \
  -H "Idempotency-Key: order-1001" \
  -H "Content-Type: application/json" \
  -d '{"amount": 500000, "currency": "NGN", "description": "Order #1001"}'
```

Send the same request again and you get the same payment back with `Idempotent-Replayed: true`.

## Tests

```bash
dotnet test
```

Integration tests start a PostgreSQL container with Testcontainers, so Docker must be running.

## API

| Method | Path | Description |
| --- | --- | --- |
| `POST` | `/v1/payments` | Create a payment |
| `GET` | `/v1/payments/{id}` | Get a payment |
| `POST` | `/v1/payments/{id}/capture` | Capture an authorized payment |
| `POST` | `/v1/payments/{id}/cancel` | Cancel a created or authorized payment |
| `POST` | `/v1/payments/{id}/refunds` | Refund all or part of a captured payment |

Errors use RFC 9457 problem details with a machine-readable `code`.
