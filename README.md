# TopUp Wallet

A small but production-shaped **mobile airtime top-up** service, built as two
microservices on the exact stack a fintech backend role asks for: **C# / ASP.NET
Core, SQL Server, RabbitMQ, Redis, MongoDB, Docker** — with the fintech
engineering that actually matters (idempotent money movement, a cross-service
saga with compensation, an append-only audit ledger, and resilience on the
external operator call).

It's deliberately small enough to read end-to-end, and every non-obvious
decision is documented in the design notes.

---

## What it does

A user tops up a phone number from their wallet balance:

```
POST /topups  { accountId, phoneNumber, amount, idempotencyKey }
```

1. **Debit** the user's wallet (exactly-once, atomic, concurrency-safe).
2. **Charge** the external mobile operator (simulated telco behind a gateway).
3. If the operator **declines**, **compensate** — refund the wallet.

The whole flow is orchestrated as a **saga** across two services talking over
RabbitMQ; money can't be lost or double-moved at any step.

---

## Architecture

```
            HTTP                     RabbitMQ (MassTransit)
 client ──────────► TopUp.Api ───────────────────────────────► Wallet.Api
                    (saga brain)   DebitWallet / RefundWallet   (money core)
                        │          ◄───────────────────────────
                        │           WalletDebited / ...Failed / WalletRefunded
                        │
                        ├─► IOperatorGateway  (external telco; Polly-wrapped)
                        └─► Redis             (rate limit + price cache)

 Wallet.Api ─► SQL Server  (balances, append-only ledger, idempotency store)
           └─► MongoDB     (append-only audit trail)
```

- **Wallet.Api** — owns money. One DB transaction per move: UPDATE balance
  (rowversion-checked) + INSERT ledger row + INSERT idempotency record. Consumes
  `DebitWallet` / `RefundWallet` commands, publishes result events.
- **TopUp.Api** — owns the flow. A MassTransit state-machine saga drives
  debit → operator → confirm/compensate. Hosts the operator gateway, Redis rate
  limiter and price cache.
- **Shared** — message contracts only (pure POCO records, no dependencies).

---

## Tech → fintech-JD mapping

| The JD asks for | Where it lives here |
|---|---|
| C# / ASP.NET Core Web API | both services (Minimal APIs, .NET 10) |
| SQL Server | `Wallet.Api` — balances, ledger, idempotency store (EF Core) |
| RabbitMQ | inter-service commands + events via MassTransit |
| Redis | rate limiting + cache-aside price lookup (`TopUp.Api`) |
| MongoDB | append-only audit log (`Wallet.Api/Audit`) |
| Docker | `docker-compose.yml` — all four backing services |
| Microservices | two bounded-context services + shared contracts |
| Messaging / REST | REST at the edges, event-driven between services |
| TDD / SOLID / mocking | xUnit + Moq; `IAuditLog` / `IOperatorGateway` seams |
| Design patterns | Saga, Compensation, Decorator, Cache-aside, Repository (EF) |

---

## Fintech engineering (the point of the project)

- **Idempotency** — every money move carries a client idempotency key stored
  with its result; a replay returns the original result, never charges twice.
- **Money correctness** — `decimal` throughout, `DECIMAL(19,4)` columns; no
  float drift.
- **Atomic + concurrency-safe** — balance + ledger + idempotency record commit
  in one transaction; optimistic concurrency via SQL `rowversion` prevents two
  debits racing on the same balance.
- **Saga + compensation** — no distributed 2-phase commit; each step commits
  locally and a failed operator charge is undone by an *opposite ledger entry*
  (append-only, fully auditable), not a rollback.
- **Append-only audit** — immutable event log in MongoDB, separate store.
- **Resilience** — Polly retry / timeout / circuit-breaker on the one
  uncontrolled external call; open circuit fails fast and the saga compensates.

Each of these is documented with the *why* behind it, tied to the exact file.

---

## Run it

Prereqs: Docker.

```bash
# everything — backing services AND both app services, built from source
docker compose up -d --build
```

Wallet.Api → http://localhost:5001, TopUp.Api → http://localhost:5002.
Both app services run in Alpine containers; compose points their connection
strings at the other containers and health-gates startup on the broker/cache.

Local dev without containers (needs the .NET 10 SDK):

```bash
docker compose up -d sqlserver rabbitmq redis mongo   # infra only
dotnet run --project src/Wallet.Api --urls http://localhost:5001
dotnet run --project src/TopUp.Api  --urls http://localhost:5002
```

### Try it

```bash
# create + fund an account
ACC=$(curl -s -X POST http://localhost:5001/accounts \
  -H 'Content-Type: application/json' \
  -d '{"ownerName":"Demo","currency":"USD","opening":100}' | jq -r .id)

# happy-path top-up  -> wallet debited, operator charged, balance 95
curl -s -X POST http://localhost:5002/topups -H 'Content-Type: application/json' \
  -d "{\"accountId\":\"$ACC\",\"phoneNumber\":\"+880171\",\"amount\":5,\"idempotencyKey\":\"ok-1\"}"

# decline path (amount .13 cents) -> debited then auto-refunded, balance back to 95
curl -s -X POST http://localhost:5002/topups -H 'Content-Type: application/json' \
  -d "{\"accountId\":\"$ACC\",\"phoneNumber\":\"+880171\",\"amount\":5.13,\"idempotencyKey\":\"fail-1\"}"

# inspect: immutable SQL ledger + Mongo audit trail
curl -s http://localhost:5001/accounts/$ACC/transactions | jq
curl -s http://localhost:5001/accounts/$ACC/audit        | jq
```

### Test

```bash
dotnet test
```

---

## Known limits (deliberate, and named on purpose)

Production-shaped, not production-complete. The gaps I'd close next, in order:

- **Transactional outbox** — result events are published best-effort after
  commit; the outbox makes "commit the debit" and "publish the event" atomic,
  closing the crash window.
- **Durable saga store** — the saga uses an in-memory repository; production
  persists state (EF/Redis/Mongo) so a restart resumes in-flight top-ups.
- **Real operator gateway** — `SimulatedOperatorGateway` → an HTTP impl (the
  Polly wrapper already handles resilience).
- **AuthN/Z, secrets in a vault, TLS/mTLS between services.**

---

Built step-by-step to be understood and defended line-by-line, not generated.
```
