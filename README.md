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
 client ─login─► Auth.Api ─► JWT access + refresh tokens
   │             (stateless)
   │  access token (Bearer) on every call below
   ▼
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

- **Auth.Api** — issues JWT access + refresh tokens on login. Stateless (no
  store): tokens are self-expiring and validated by signature. Clients send the
  access token as a `Bearer` header to the other services.
- **Wallet.Api** — owns money. One DB transaction per move: UPDATE balance
  (rowversion-checked) + INSERT ledger row + INSERT idempotency record. Consumes
  `DebitWallet` / `RefundWallet` commands, publishes result events. Enforces
  account ownership (a user can only touch their own wallet).
- **TopUp.Api** — owns the flow. A MassTransit state-machine saga drives
  debit → operator → confirm/compensate. Hosts the operator gateway, Redis rate
  limiter and price cache.
- **Shared** — message contracts + JWT conventions (pure POCO, no dependencies).

All three services validate the same JWT (shared signing key, issuer, audience);
access tokens only (a refresh token can't call the APIs). Each account is bound
to its creator's user id, and ownership is checked both at the HTTP edge and on
the internal saga path.

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
- **Card funding via a payment gateway** — money *in*: the browser tokenizes the
  card with the PSP (the PAN never hits the API), the wallet is credited only on
  the PSP's **signed webhook** (not the sync response), and the credit is
  idempotent on the PSP event id. Forged/unsigned webhooks are rejected. Decline
  and 3-D Secure are modelled branches. `IPaymentGateway` + a simulator, swappable
  for a real PSP (Stripe/Adyen/SSLCommerz).

Each of these is documented with the *why* behind it, tied to the exact file.

---

## Run it

Prereqs: Docker.

```bash
# everything — backing services AND both app services, built from source
docker compose up -d --build
```

Auth.Api → http://localhost:5003, Wallet.Api → http://localhost:5001,
TopUp.Api → http://localhost:5002. All run in Alpine containers; compose points
their connection strings at the other containers, passes a shared JWT signing
key, and health-gates startup on the broker/cache.

Local dev without containers (needs the .NET 10 SDK):

```bash
docker compose up -d sqlserver rabbitmq redis mongo   # infra only
dotnet run --project src/Auth.Api   --urls http://localhost:5003
dotnet run --project src/Wallet.Api --urls http://localhost:5001
dotnet run --project src/TopUp.Api  --urls http://localhost:5002
```

### Try it

**Every endpoint except `/health` and the PSP webhook needs a JWT.** You log in
once at Auth.Api, then send the access token as a `Bearer` header on every call
to Wallet.Api and TopUp.Api. (Demo login accepts any non-empty credentials; the
username becomes the token's subject and the account's owner.)

```bash
# ── 1. LOG IN → access token (send it as a Bearer header on everything below) ──
TOKEN=$(curl -s -X POST http://localhost:5003/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"demo","password":"any"}' | jq -r .accessToken)
AUTH="Authorization: Bearer $TOKEN"

# ── 2. create an account (starts at 0; owned by "demo") ──
ACC=$(curl -s -X POST http://localhost:5001/accounts -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -d '{"ownerName":"Demo","currency":"USD","opening":0}' | jq -r .id)

# ── 3. FUND IT with a card (money IN via the payment gateway) ──
# The browser would tokenize the card with the PSP; here we pass a demo token.
# This creates a PENDING deposit — the balance is NOT credited yet.
DEP=$(curl -s -X POST http://localhost:5001/accounts/$ACC/deposits -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -d '{"paymentToken":"tok_ok","amount":100}' | jq -r .depositId)

# the money lands only when the PSP's SIGNED webhook confirms the charge.
# get a correctly-signed webhook payload (demo helper), then POST it:
PAYLOAD=$(curl -s -X POST http://localhost:5001/accounts/$ACC/deposits/$DEP/webhook-payload -H "$AUTH")
BODY=$(echo "$PAYLOAD" | jq -r .body); SIG=$(echo "$PAYLOAD" | jq -r .signature)
curl -s -X POST http://localhost:5001/webhooks/payment \
  -H "X-Signature: $SIG" -H 'Content-Type: application/json' -d "$BODY"
# balance is now 100 (POST the same webhook again → still 100: idempotent)

# ── 4. TOP UP a phone (money OUT via the saga) ──
# happy path: wallet debited, operator charged → balance 95
curl -s -X POST http://localhost:5002/topups -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"accountId\":\"$ACC\",\"phoneNumber\":\"+880171\",\"amount\":5,\"idempotencyKey\":\"ok-1\"}"

# decline path (amount ending .13): debited then auto-refunded → back to 95
curl -s -X POST http://localhost:5002/topups -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"accountId\":\"$ACC\",\"phoneNumber\":\"+880171\",\"amount\":5.13,\"idempotencyKey\":\"fail-1\"}"

# ── 5. inspect: balance, immutable SQL ledger, Mongo audit trail ──
curl -s http://localhost:5001/accounts/$ACC              -H "$AUTH" | jq
curl -s http://localhost:5001/accounts/$ACC/transactions -H "$AUTH" | jq
curl -s http://localhost:5001/accounts/$ACC/audit        -H "$AUTH" | jq

# ── 6. (optional) ownership: a DIFFERENT user can't touch this account ──
OTHER=$(curl -s -X POST http://localhost:5003/auth/login -H 'Content-Type: application/json' \
  -d '{"username":"mallory","password":"any"}' | jq -r .accessToken)
curl -s -o /dev/null -w 'mallory reads demo account -> HTTP %{http_code}\n' \
  http://localhost:5001/accounts/$ACC -H "Authorization: Bearer $OTHER"   # -> 403
```

> No token (or a refresh token used as a bearer) → **401**. Someone else's account
> → **403**. The PSP webhook is the one open endpoint — it's authenticated by its
> HMAC **signature**, not a JWT; a forged signature → **401**.

### Test

```bash
dotnet test
```

### Observability

All three services are instrumented with **OpenTelemetry** — traces, metrics and
logs — exported over **OTLP** (the vendor-neutral format). Auto-instrumentation
covers ASP.NET Core, HttpClient, EF Core (Wallet), Redis (TopUp) and **MassTransit**,
so a single top-up is one distributed trace spanning HTTP → RabbitMQ → Wallet →
the external gateway. Custom business metrics (`funding.deposits.created`,
`funding.deposits.credited`, `funding.webhook.rejected`) are emitted too.

**No backend is bundled.** How you view the data:

- **Export to a backend (the real way).** Point the services at any OTLP
  collector/backend and browse in its UI — nothing in the app changes:

  ```bash
  # example: run Jaeger and send traces to it
  docker run -d --name jaeger -p 16686:16686 -p 4317:4317 jaegertracing/all-in-one
  OTEL_EXPORTER_OTLP_ENDPOINT=http://host.docker.internal:4317 docker compose up -d
  # open http://localhost:16686 and follow a top-up across all services
  ```

  Any OTLP backend works the same way: Grafana Tempo/Loki/Prometheus, Datadog,
  Honeycomb, or an OpenTelemetry Collector fanning out to several.

- **See it with no infra (dev toggle).** Set `OTEL_CONSOLE=true` to print traces,
  metrics and logs straight to the container logs — no backend needed:

  ```bash
  OTEL_CONSOLE=true docker compose up -d --build
  docker compose logs -f wallet-api      # watch spans/metrics/logs scroll
  ```

  You'll see e.g. a `POST /accounts` server span and its `WalletDb` EF span sharing
  one `TraceId`. Noisy — dev only, off by default.

- **Nothing set = instrumented but silent.** With neither `OTEL_EXPORTER_OTLP_ENDPOINT`
  nor `OTEL_CONSOLE`, telemetry is generated but not exported anywhere — the
  instrumentation simply costs nothing until a backend (or the console toggle) is on.

OTLP is a **push** model (the app pushes to the collector); you don't poll the
app for telemetry. Metrics *can* also be exposed for Prometheus scraping by adding
that exporter, if a pull model is preferred.

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
- **Real user management** — `Auth.Api` login currently accepts any non-empty
  credentials (the token subject is the username); the seam is where a user store
  / IdP (Auth0, Entra ID) plugs in.
- **Revocable refresh tokens** — tokens are stateless and self-expiring, so there
  is no pre-expiry revocation (no server-side logout-kills-session, no
  refresh-reuse detection). Production persists hashed refresh tokens or delegates
  to an IdP.
- **Secrets in a vault** — the JWT signing key and DB password come from env in
  compose; production sources them from a vault (Key Vault / Secrets Manager).
- **Transport security (TLS/mTLS)** — left to the platform, not baked into the
  app: in a cluster, an Istio sidecar provides mTLS between services
  transparently; otherwise each service sits behind an HTTPS ingress / public
  DNS + TLS cert. Caller *identity* is already carried by the JWT, so the app
  doesn't duplicate it.

---

Built step-by-step to be understood and defended line-by-line, not generated.
```
