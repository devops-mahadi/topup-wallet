using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Trace;
using Shared.Auth;
using Shared.Contracts;
using Shared.Observability;
using StackExchange.Redis;
using TopUp.Api.Gateways;
using TopUp.Api.Infrastructure;
using TopUp.Api.Pricing;
using TopUp.Api.Saga;

// Keep JWT claim types verbatim ("sub" stays "sub", not remapped to a long URI).
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// ---- Authentication: validate JWT access tokens (same key/issuer as Auth.Api) ----
var signingKey = builder.Configuration[JwtConventions.SigningKeyConfigPath]
    ?? throw new InvalidOperationException(
        $"Missing JWT signing key at '{JwtConventions.SigningKeyConfigPath}'. Set Jwt__SigningKey.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = JwtConventions.Issuer,
            ValidateAudience = true,
            ValidAudience = JwtConventions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        opt.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                var type = ctx.Principal?.FindFirst(JwtConventions.TokenTypeClaim)?.Value;
                if (type != JwtConventions.AccessTokenType)
                    ctx.Fail("Access token required.");
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// Redis connection — one multiplexer for the whole app (it's thread-safe and
// multiplexes; you do NOT open a connection per call).
var redisConn = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConn));

// OpenTelemetry over OTLP. TopUp adds Redis + MassTransit tracing so cache calls
// and the saga's bus messages appear in the trace, with context propagating
// across RabbitMQ so a whole top-up is one distributed trace. No backend bundled.
builder.Services.AddObservability(builder.Configuration, "topup-api",
    configureTracing: t => t
        .AddRedisInstrumentation()
        .AddSource("MassTransit"));

// Redis-backed rate limiter (fraud guard) + price cache (cache-aside).
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddSingleton<PriceService>();

// The external operator boundary. The simulator is the raw impl; the resilient
// decorator wraps it in Polly (retry/timeout/circuit-breaker). The saga depends
// only on IOperatorGateway and is oblivious to the wrapping (decorator pattern).
builder.Services.AddSingleton<SimulatedOperatorGateway>();
builder.Services.AddSingleton<IOperatorGateway>(sp =>
    new ResilientOperatorGateway(
        sp.GetRequiredService<SimulatedOperatorGateway>(),
        sp.GetRequiredService<ILogger<ResilientOperatorGateway>>()));

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
        var rabbit = builder.Configuration.GetConnectionString("RabbitMq") ?? "localhost";
        cfg.Host(rabbit, "/", h => { h.Username("guest"); h.Password("guest"); });
        // Auto-create the saga's receive endpoint + bind published events to it.
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "topup" }));

// --- Start a top-up (kicks off the saga) ---
// Requires a valid access token. The caller's id (JWT sub) is carried into the
// saga so the Wallet can verify the account belongs to this user before debiting.
// Returns 202 Accepted + the TopUpId; the flow then runs async across services.
app.MapPost("/topups", async (TopUpRequest req, IBus bus, RateLimiter limiter, PriceService prices, ClaimsPrincipal user) =>
{
    var userId = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                 ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // Fraud guard: cap top-ups per account per minute (Redis-backed, cross-instance).
    if (!await limiter.AllowAsync(req.AccountId, maxPerWindow: 5, window: TimeSpan.FromMinutes(1)))
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);

    // Look up the fee (cache-aside via Redis) — returned to the caller up front.
    var fee = await prices.GetFeeAsync(req.Amount);

    var topUpId = Guid.CreateVersion7();   // correlation id for the whole saga
    await bus.Publish(new StartTopUp(
        topUpId, req.AccountId, userId, req.PhoneNumber, req.Amount, req.IdempotencyKey));

    return Results.Accepted($"/topups/{topUpId}",
        new { topUpId, status = "accepted", fee });
}).RequireAuthorization();

app.Run();

record TopUpRequest(Guid AccountId, string PhoneNumber, decimal Amount, string IdempotencyKey);
