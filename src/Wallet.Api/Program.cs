using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Wallet.Api.Audit;
using Wallet.Api.Data;
using Wallet.Api.Domain;
using Wallet.Api.Services;

// Tell the Mongo driver how to store Guids. Modern driver requires this to be
// explicit (no silent default) — Standard = the current, portable representation.
BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Register the DbContext against SQL Server (Azure SQL Edge in Docker).
// Connection string comes from config (appsettings) — never hardcoded here.
builder.Services.AddDbContext<WalletDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("WalletDb")));

// Mongo audit log — Singleton (MongoClient is thread-safe + pools connections).
builder.Services.AddSingleton<AuditLog>();

// Scoped: one WalletService (and its DbContext) per HTTP request.
builder.Services.AddScoped<WalletService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Dev convenience: apply migrations at startup so the DB/schema exist.
// (In production you'd run migrations as a separate deploy step, not at boot.)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
    db.Database.Migrate();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "wallet" }));

// --- Create an account (seed/testing) ---
app.MapPost("/accounts", async (CreateAccount req, WalletDbContext db) =>
{
    // CreateVersion7() = time-ordered UUIDv7: generated app-side (before the DB round-trip,
    // so the id is known immediately) AND sequential, keeping index locality — avoids the
    // page-split fragmentation that random v4 Guids (Guid.NewGuid) cause.
    var acc = new Account { Id = Guid.CreateVersion7(), OwnerName = req.OwnerName, Currency = req.Currency, Balance = req.Opening };
    db.Accounts.Add(acc);
    await db.SaveChangesAsync();
    return Results.Created($"/accounts/{acc.Id}", new { acc.Id, acc.OwnerName, acc.Currency, acc.Balance });
});

app.MapGet("/accounts/{id:guid}", async (Guid id, WalletDbContext db) =>
{
    var acc = await db.Accounts.FindAsync(id);
    return acc is null ? Results.NotFound() : Results.Ok(new { acc.Id, acc.OwnerName, acc.Currency, acc.Balance });
});

// --- Debit / Credit (idempotent, atomic, concurrency-safe) ---
app.MapPost("/accounts/{id:guid}/debit", async (Guid id, MoneyOp op, WalletService svc) =>
    await Handle(() => svc.DebitAsync(new MoneyRequest(id, op.Amount, op.IdempotencyKey))));

app.MapPost("/accounts/{id:guid}/credit", async (Guid id, MoneyOp op, WalletService svc) =>
    await Handle(() => svc.CreditAsync(new MoneyRequest(id, op.Amount, op.IdempotencyKey))));

// --- Ledger (immutable transaction history, from SQL Server) ---
app.MapGet("/accounts/{id:guid}/transactions", async (Guid id, WalletDbContext db) =>
    Results.Ok(await db.Transactions.AsNoTracking()
        .Where(t => t.AccountId == id)
        .OrderByDescending(t => t.CreatedAtUtc)
        .ToListAsync()));

// --- Audit trail (append-only event log, from MongoDB) ---
app.MapGet("/accounts/{id:guid}/audit", async (Guid id, AuditLog audit) =>
    Results.Ok(await audit.ForAccountAsync(id)));

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
