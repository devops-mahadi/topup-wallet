using Microsoft.EntityFrameworkCore;
using Wallet.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Register the DbContext against SQL Server (Azure SQL Edge in Docker).
// Connection string comes from config (appsettings) — never hardcoded here.
builder.Services.AddDbContext<WalletDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("WalletDb")));

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

app.Run();
