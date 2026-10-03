using MassTransit;
using Shared.Contracts;
using TopUp.Api.Gateways;
using TopUp.Api.Saga;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// The external operator boundary — simulated impl for dev (swap for HTTP in prod).
builder.Services.AddSingleton<IOperatorGateway, SimulatedOperatorGateway>();

// MassTransit: TopUp hosts the ORCHESTRATION SAGA. It also needs to send commands
// to the Wallet's queues, so it talks to the same RabbitMQ broker.
builder.Services.AddMassTransit(x =>
{
    x.AddSagaStateMachine<TopUpStateMachine, TopUpState>()
        // In-memory saga repository: simplest store, fine for dev/demo. Production
        // uses a durable repo (EF Core / Redis / Mongo) so saga state survives
        // restarts — the state machine code is identical, only the repo swaps.
        .InMemoryRepository();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host("localhost", "/", h => { h.Username("guest"); h.Password("guest"); });
        // Auto-create the saga's receive endpoint + bind published events to it.
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "topup" }));

// --- Start a top-up (kicks off the saga) ---
// Returns 202 Accepted + the TopUpId; the flow then runs async across services.
app.MapPost("/topups", async (TopUpRequest req, IBus bus) =>
{
    var topUpId = Guid.CreateVersion7();   // correlation id for the whole saga
    await bus.Publish(new StartTopUp(
        topUpId, req.AccountId, req.PhoneNumber, req.Amount, req.IdempotencyKey));

    return Results.Accepted($"/topups/{topUpId}", new { topUpId, status = "accepted" });
});

app.Run();

record TopUpRequest(Guid AccountId, string PhoneNumber, decimal Amount, string IdempotencyKey);
