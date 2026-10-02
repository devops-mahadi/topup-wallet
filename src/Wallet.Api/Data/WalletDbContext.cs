using Microsoft.EntityFrameworkCore;
using Wallet.Api.Domain;

namespace Wallet.Api.Data;

public class WalletDbContext : DbContext
{
    public WalletDbContext(DbContextOptions<WalletDbContext> options) : base(options) { }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<WalletTransaction> Transactions => Set<WalletTransaction>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.HasKey(a => a.Id);
            // DECIMAL(19,4): 19 total digits, 4 after the point. Exact currency math.
            e.Property(a => a.Balance).HasColumnType("decimal(19,4)");
            e.Property(a => a.Currency).HasMaxLength(3);
            e.Property(a => a.OwnerName).HasMaxLength(200);
            // IsRowVersion() → SQL Server 'rowversion' column, the concurrency token.
            e.Property(a => a.RowVersion).IsRowVersion();
        });

        b.Entity<WalletTransaction>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Amount).HasColumnType("decimal(19,4)");
            e.Property(t => t.BalanceAfter).HasColumnType("decimal(19,4)");
            e.Property(t => t.IdempotencyKey).HasMaxLength(100);
            // Index for fast lookup of an account's ledger + duplicate-key checks.
            e.HasIndex(t => t.AccountId);
            e.HasIndex(t => t.IdempotencyKey);
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.HasKey(i => i.Key);                       // key is the PK → DB enforces uniqueness
            e.Property(i => i.Key).HasMaxLength(100);
            e.Property(i => i.ResultingBalance).HasColumnType("decimal(19,4)");
        });
    }
}
