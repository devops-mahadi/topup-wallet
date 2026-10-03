using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Shared.Auth;
using Wallet.Api.Audit;
using Wallet.Api.Consumers;
using Wallet.Api.Data;
using Wallet.Api.Domain;
using Wallet.Api.Services;

// Tell the Mongo driver how to store Guids. Modern driver requires this to be
// explicit (no silent default) — Standard = the current, portable representation.
BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

// Keep JWT claim types verbatim ("sub" stays "sub", not remapped to a long URI).
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Register the DbContext against SQL Server (Azure SQL Edge in Docker).
// Connection string comes from config (appsettings) — never hardcoded here.
builder.Services.AddDbContext<WalletDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("WalletDb")));

// Mongo audit log — Singleton (MongoClient is thread-safe + pools connections).
builder.Services.AddSingleton<IAuditLog, AuditLog>();

// Scoped: one WalletService (and its DbContext) per HTTP request / message.
builder.Services.AddScoped<WalletService>();

// ---- Authentication: validate JWT bearer tokens ----
// The signing key is a SECRET read from config (env var / user-secrets / vault),
// never hardcoded. In dev a fallback is provided so the sample runs; in any real
// environment the key MUST come from the environment (see appsettings note).
var signingKey = builder.Configuration[JwtConventions.SigningKeyConfigPath]
    ?? throw new InvalidOperationException(
        $"Missing JWT signing key at config '{JwtConventions.SigningKeyConfigPath}'. " +
        "Set it via environment (Jwt__SigningKey) or user-secrets.");

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
            ClockSkew = TimeSpan.FromSeconds(30)   // tight skew for money APIs
        };
        // Accept ACCESS tokens only — a refresh token can't be used to call the API.
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

// MassTransit over RabbitMQ. Wallet is a COMMAND HANDLER: it consumes
// DebitWallet / RefundWallet and publishes result events the saga awaits.
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<DebitWalletConsumer>();
    x.AddConsumer<RefundWalletConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rabbit = builder.Configuration.GetConnectionString("RabbitMq") ?? "localhost";
        cfg.Host(rabbit, "/", h => { h.Username("guest"); h.Password("guest"); });

        // Bind the consumers to the exact queue names the saga sends to.
        cfg.ReceiveEndpoint("wallet-debit", e =>
        {
            e.ConfigureConsumer<DebitWalletConsumer>(ctx);
            // Retry transient faults (deadlocks, broker blips) before dead-lettering.
            e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromMilliseconds(500)));
        });
        cfg.ReceiveEndpoint("wallet-refund", e =>
        {
            e.ConfigureConsumer<RefundWalletConsumer>(ctx);
            e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromMilliseconds(500)));
        });
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Dev convenience: apply migrations at startup so the DB/schema exist.
// (In production you'd run migrations as a separate deploy step, not at boot.)
// If SQL Server isn't up yet (it may still be booting in Docker), Migrate throws
// and the process exits — we let it crash. Docker's restart policy then restarts
// the container, and it keeps retrying until SQL is ready. Crash-only / let-it-
// crash: the orchestrator owns restarts, not bespoke retry code in the app.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
    db.Database.Migrate();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "wallet" }));

// Helper: the caller's user id (JWT "sub"). Present on every authorized endpoint.
static string CallerId(ClaimsPrincipal user) =>
    user.FindFirstValue(JwtRegisteredClaimNames.Sub)
    ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
    ?? throw new InvalidOperationException("Authenticated caller has no subject claim.");

// Helper: load an account the caller is allowed to touch, or an error result.
// Returns 404 if missing, 403 if it belongs to a different user (kills the IDOR —
// a valid token for user A still can't read/move user B's account).
static async Task<(Account? acc, IResult? error)> LoadOwned(WalletDbContext db, Guid id, ClaimsPrincipal user)
{
    var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id);
    if (acc is null) return (null, Results.NotFound());
    if (acc.UserId != CallerId(user)) return (null, Results.Forbid());
    return (acc, null);
}

// --- Create an account --- owned by the authenticated caller.
app.MapPost("/accounts", async (CreateAccount req, WalletDbContext db, ClaimsPrincipal user) =>
{
    // UUIDv7 id (time-ordered: app-side + index-friendly). UserId = caller's sub,
    // so this wallet is bound to the logged-in user from creation.
    var acc = new Account
    {
        Id = Guid.CreateVersion7(),
        UserId = CallerId(user),
        OwnerName = req.OwnerName,
        Currency = req.Currency,
        Balance = req.Opening
    };
    db.Accounts.Add(acc);
    await db.SaveChangesAsync();
    return Results.Created($"/accounts/{acc.Id}", new { acc.Id, acc.OwnerName, acc.Currency, acc.Balance });
}).RequireAuthorization();

app.MapGet("/accounts/{id:guid}", async (Guid id, WalletDbContext db, ClaimsPrincipal user) =>
{
    var (acc, error) = await LoadOwned(db, id, user);
    return error ?? Results.Ok(new { acc!.Id, acc.OwnerName, acc.Currency, acc.Balance });
}).RequireAuthorization();

// --- Debit / Credit --- caller must own the account.
app.MapPost("/accounts/{id:guid}/debit", async (Guid id, MoneyOp op, WalletService svc, WalletDbContext db, ClaimsPrincipal user) =>
{
    var (_, error) = await LoadOwned(db, id, user);
    return error ?? await Handle(() => svc.DebitAsync(new MoneyRequest(id, op.Amount, op.IdempotencyKey)));
}).RequireAuthorization();

app.MapPost("/accounts/{id:guid}/credit", async (Guid id, MoneyOp op, WalletService svc, WalletDbContext db, ClaimsPrincipal user) =>
{
    var (_, error) = await LoadOwned(db, id, user);
    return error ?? await Handle(() => svc.CreditAsync(new MoneyRequest(id, op.Amount, op.IdempotencyKey)));
}).RequireAuthorization();

// --- Ledger (immutable transaction history, from SQL Server) ---
app.MapGet("/accounts/{id:guid}/transactions", async (Guid id, WalletDbContext db, ClaimsPrincipal user) =>
{
    var (_, error) = await LoadOwned(db, id, user);
    return error ?? Results.Ok(await db.Transactions.AsNoTracking()
        .Where(t => t.AccountId == id)
        .OrderByDescending(t => t.CreatedAtUtc)
        .ToListAsync());
}).RequireAuthorization();

// --- Audit trail (append-only event log, from MongoDB) ---
app.MapGet("/accounts/{id:guid}/audit", async (Guid id, IAuditLog audit, WalletDbContext db, ClaimsPrincipal user) =>
{
    var (_, error) = await LoadOwned(db, id, user);
    return error ?? Results.Ok(await audit.ForAccountAsync(id));
}).RequireAuthorization();

app.Run();

// Translate domain outcomes into clean HTTP responses.
static async Task<IResult> Handle(Func<Task<MoneyResult>> action)
{
    try
    {
        var r = await action();
        return Results.Ok(new { r.TransactionId, r.Balance, r.WasReplay });
    }
    catch (InsufficientFundsException) { return Results.BadRequest(new { error = "insufficient_funds" }); }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "account_not_found" }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
}

record CreateAccount(string OwnerName, string Currency, decimal Opening);
record MoneyOp(decimal Amount, string IdempotencyKey);
