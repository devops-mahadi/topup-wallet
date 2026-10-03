using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Wallet.Api.Audit;
using Wallet.Api.Data;
using Wallet.Api.Domain;
using Wallet.Api.Services;
using Xunit;

namespace Wallet.Tests;

/// <summary>
/// Unit tests for the money core. We use EF Core InMemory (no real SQL) and a
/// mocked IAuditLog (no real Mongo) so the tests are fast and hermetic. InMemory
/// doesn't support real transactions, so we silence that warning — these tests
/// cover the LOGIC (idempotency, balance math, decimal exactness), not the SQL
/// transaction/rowversion behaviour (that's integration-tested against real SQL).
/// </summary>
public class WalletServiceTests
{
    private static WalletDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WalletDbContext>()
            .UseInMemoryDatabase($"wallet-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static WalletService NewService(WalletDbContext db) =>
        new(db, Mock.Of<IAuditLog>());   // audit is best-effort; a no-op mock is fine

    private static async Task<Account> SeedAccount(WalletDbContext db, decimal balance)
    {
        var acc = new Account { Id = Guid.CreateVersion7(), OwnerName = "T", Currency = "USD", Balance = balance };
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();
        return acc;
    }

    [Fact]
    public async Task Credit_increases_balance()
    {
        using var db = NewDb();
        var acc = await SeedAccount(db, 100m);
        var svc = NewService(db);

        var r = await svc.CreditAsync(new MoneyRequest(acc.Id, 25m, "k1"));

        Assert.False(r.WasReplay);
        Assert.Equal(125m, r.Balance);
    }

    [Fact]
    public async Task Debit_decreases_balance()
    {
        using var db = NewDb();
        var acc = await SeedAccount(db, 100m);
        var svc = NewService(db);

        var r = await svc.DebitAsync(new MoneyRequest(acc.Id, 30m, "k1"));

        Assert.Equal(70m, r.Balance);
    }

    [Fact]
    public async Task Debit_over_balance_throws_InsufficientFunds_and_leaves_balance_unchanged()
    {
        using var db = NewDb();
        var acc = await SeedAccount(db, 10m);
        var svc = NewService(db);

        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => svc.DebitAsync(new MoneyRequest(acc.Id, 50m, "k1")));

        var reloaded = await db.Accounts.FindAsync(acc.Id);
        Assert.Equal(10m, reloaded!.Balance);
    }

    [Fact]
    public async Task Same_idempotency_key_replayed_does_not_double_charge()
    {
        using var db = NewDb();
        var acc = await SeedAccount(db, 100m);
        var svc = NewService(db);

        var first = await svc.DebitAsync(new MoneyRequest(acc.Id, 40m, "dup-key"));
        var second = await svc.DebitAsync(new MoneyRequest(acc.Id, 40m, "dup-key"));

        Assert.False(first.WasReplay);
        Assert.True(second.WasReplay);                       // second is a replay
        Assert.Equal(first.TransactionId, second.TransactionId); // same original txn
        Assert.Equal(60m, second.Balance);                   // charged ONCE, not twice

        var reloaded = await db.Accounts.FindAsync(acc.Id);
        Assert.Equal(60m, reloaded!.Balance);
    }

    [Fact]
    public async Task Non_positive_amount_is_rejected()
    {
        using var db = NewDb();
        var acc = await SeedAccount(db, 100m);
        var svc = NewService(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.DebitAsync(new MoneyRequest(acc.Id, 0m, "k1")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.CreditAsync(new MoneyRequest(acc.Id, -5m, "k2")));
    }

    [Fact]
    public async Task Decimal_math_is_exact_no_float_drift()
    {
        using var db = NewDb();
        var acc = await SeedAccount(db, 0m);
        var svc = NewService(db);

        // 0.1 + 0.2 would drift to 0.30000000000000004 in double. decimal is exact.
        await svc.CreditAsync(new MoneyRequest(acc.Id, 0.1m, "a"));
        var r = await svc.CreditAsync(new MoneyRequest(acc.Id, 0.2m, "b"));

        Assert.Equal(0.3m, r.Balance);
    }

    [Fact]
    public async Task Debit_writes_a_ledger_entry_with_balance_snapshot()
    {
        using var db = NewDb();
        var acc = await SeedAccount(db, 100m);
        var svc = NewService(db);

        await svc.DebitAsync(new MoneyRequest(acc.Id, 15m, "k1"));

        var txn = await db.Transactions.SingleAsync(t => t.AccountId == acc.Id);
        Assert.Equal(TransactionType.Debit, txn.Type);
        Assert.Equal(15m, txn.Amount);
        Assert.Equal(85m, txn.BalanceAfter);   // snapshot of balance after the move
    }
}
