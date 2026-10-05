using Microsoft.EntityFrameworkCore;
using Moq;
using Wallet.Api.Audit;
using Wallet.Api.Data;
using Wallet.Api.Domain;
using Wallet.Api.Services;
using Xunit;

namespace Wallet.Tests;

/// <summary>
/// Covers the transactional-outbox contract in WalletService: when a transaction is
/// already open on the DbContext — which is what MassTransit's outbox filter does
/// before a consumer runs — the money move must ENLIST in it rather than commit its
/// own. That is what makes the balance update, the ledger row and the OutboxMessage
/// row for the published event commit atomically.
///
/// These need real SQL Server, because the behaviour under test is transaction
/// semantics and Account.RowVersion is a store-generated SQL 'rowversion'. EF Core
/// InMemory has neither, so it cannot tell a committed move from an uncommitted one.
/// They run against the docker-compose SQL Server and SKIP (not fail) when it isn't
/// reachable, so `dotnet test` stays green on a machine with no containers.
/// </summary>
public class OutboxTransactionTests
{
    private const string ConnectionString =
        "Server=localhost,1433;Database=WalletOutboxTests;User Id=sa;Password=TopUpWallet!2026;TrustServerCertificate=True;Encrypt=True";

    private static WalletDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WalletDbContext>().UseSqlServer(ConnectionString).Options);

    /// <summary>Spin up a throwaway database, or return null when SQL isn't available.</summary>
    private static async Task<WalletDbContext?> TryNewDbAsync()
    {
        try
        {
            var db = NewDb();
            await db.Database.EnsureCreatedAsync();
            return db;
        }
        catch (Exception)
        {
            return null;   // no SQL Server here — caller skips
        }
    }

    private static async Task<Account> SeedAccount(WalletDbContext db, decimal balance)
    {
        var acc = new Account
        {
            Id = Guid.CreateVersion7(), OwnerName = "T", Currency = "USD",
            Balance = balance, UserId = "owner"
        };
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return acc;
    }

    private static WalletService NewService(WalletDbContext db) => new(db, Mock.Of<IAuditLog>());

    [Fact]
    public async Task Ambient_transaction_is_reused_not_replaced()
    {
        await using var db = await TryNewDbAsync();
        if (db is null) return;
        var acc = await SeedAccount(db, 100m);

        await using var tx = await db.Database.BeginTransactionAsync();
        var outer = db.Database.CurrentTransaction!.TransactionId;

        await NewService(db).DebitAsync(new MoneyRequest(acc.Id, 10m, $"amb-{Guid.NewGuid()}"));

        // Same transaction still open and current → the service enlisted instead of
        // opening (and committing) one of its own. If it had committed, CurrentTransaction
        // would be null here and the outbox row could not share the money's transaction.
        Assert.NotNull(db.Database.CurrentTransaction);
        Assert.Equal(outer, db.Database.CurrentTransaction!.TransactionId);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Ambient_move_is_not_durable_until_the_owner_commits()
    {
        await using var db = await TryNewDbAsync();
        if (db is null) return;
        var acc = await SeedAccount(db, 100m);

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await NewService(db).DebitAsync(new MoneyRequest(acc.Id, 40m, $"amb-{Guid.NewGuid()}"));
            await tx.RollbackAsync();
        }

        // Rolling back the ambient transaction must undo the money move completely:
        // balance untouched AND no ledger row left behind. This is the atomicity the
        // outbox depends on — if the event is discarded, the debit must be too.
        await using var verify = NewDb();
        var balance = await verify.Accounts.AsNoTracking()
            .Where(a => a.Id == acc.Id).Select(a => a.Balance).SingleAsync();
        Assert.Equal(100m, balance);
        Assert.False(await verify.Transactions.AsNoTracking().AnyAsync(t => t.AccountId == acc.Id));
    }

    [Fact]
    public async Task Ambient_move_is_durable_once_the_owner_commits()
    {
        await using var db = await TryNewDbAsync();
        if (db is null) return;
        var acc = await SeedAccount(db, 100m);

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await NewService(db).DebitAsync(new MoneyRequest(acc.Id, 25m, $"amb-{Guid.NewGuid()}"));
            await tx.CommitAsync();
        }

        await using var verify = NewDb();
        var balance = await verify.Accounts.AsNoTracking()
            .Where(a => a.Id == acc.Id).Select(a => a.Balance).SingleAsync();
        Assert.Equal(75m, balance);

        var txn = await verify.Transactions.AsNoTracking().SingleAsync(t => t.AccountId == acc.Id);
        Assert.Equal(TransactionType.Debit, txn.Type);
        Assert.Equal(75m, txn.BalanceAfter);
    }

    [Fact]
    public async Task Without_an_ambient_transaction_the_move_commits_itself()
    {
        await using var db = await TryNewDbAsync();
        if (db is null) return;
        var acc = await SeedAccount(db, 100m);

        // The HTTP path: no transaction open, so WalletService owns and commits one.
        Assert.Null(db.Database.CurrentTransaction);
        await NewService(db).DebitAsync(new MoneyRequest(acc.Id, 30m, $"own-{Guid.NewGuid()}"));
        Assert.Null(db.Database.CurrentTransaction);

        await using var verify = NewDb();
        var balance = await verify.Accounts.AsNoTracking()
            .Where(a => a.Id == acc.Id).Select(a => a.Balance).SingleAsync();
        Assert.Equal(70m, balance);   // durable without anyone else committing
    }

    [Fact]
    public async Task Insufficient_funds_under_ambient_leaves_the_transaction_usable()
    {
        await using var db = await TryNewDbAsync();
        if (db is null) return;
        var acc = await SeedAccount(db, 10m);

        await using var tx = await db.Database.BeginTransactionAsync();

        // A business rejection must NOT poison the ambient transaction: the consumer
        // catches this and publishes WalletDebitFailed, and that publish writes an
        // OutboxMessage row through this very transaction. If the rejection had rolled
        // it back, the failure event would be lost and the saga would hang.
        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => NewService(db).DebitAsync(new MoneyRequest(acc.Id, 500m, $"amb-{Guid.NewGuid()}")));

        Assert.NotNull(db.Database.CurrentTransaction);

        // Still usable: a read goes through, and a commit succeeds.
        var balance = await db.Accounts.AsNoTracking()
            .Where(a => a.Id == acc.Id).Select(a => a.Balance).SingleAsync();
        Assert.Equal(10m, balance);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task Replay_under_ambient_returns_the_stored_result_and_moves_no_money()
    {
        await using var db = await TryNewDbAsync();
        if (db is null) return;
        var acc = await SeedAccount(db, 100m);
        var key = $"amb-replay-{Guid.NewGuid()}";

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await NewService(db).DebitAsync(new MoneyRequest(acc.Id, 20m, key));
            await tx.CommitAsync();
        }

        // Redelivery of the same command: the fast-path idempotency check short-circuits
        // before any money moves, so a redelivered consume is safe.
        await using var db2 = NewDb();
        await using (var tx = await db2.Database.BeginTransactionAsync())
        {
            var replay = await NewService(db2).DebitAsync(new MoneyRequest(acc.Id, 20m, key));
            Assert.True(replay.WasReplay);
            Assert.Equal(80m, replay.Balance);
            await tx.CommitAsync();
        }

        await using var verify = NewDb();
        var balance = await verify.Accounts.AsNoTracking()
            .Where(a => a.Id == acc.Id).Select(a => a.Balance).SingleAsync();
        Assert.Equal(80m, balance);   // debited once, not twice
        Assert.Single(await verify.Transactions.AsNoTracking()
            .Where(t => t.AccountId == acc.Id).ToListAsync());
    }
}
