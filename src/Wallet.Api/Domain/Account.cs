namespace Wallet.Api.Domain;

/// <summary>
/// A user's wallet. Holds a balance in a single currency.
/// Money is ALWAYS decimal — never float/double (binary floating point
/// can't represent decimal fractions exactly → rounding drift on balances).
/// </summary>
public class Account
{
    public Guid Id { get; set; }

    public string OwnerName { get; set; } = string.Empty;

    /// <summary>ISO 4217 currency code, e.g. "USD", "BDT".</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Current balance. Mapped to SQL DECIMAL(19,4) in the DbContext.</summary>
    public decimal Balance { get; set; }

    /// <summary>
    /// Optimistic-concurrency token. SQL Server 'rowversion' — auto-updated on
    /// every write. EF adds "WHERE RowVersion = @original" to updates; if another
    /// transaction changed the row first, 0 rows match → DbUpdateConcurrencyException,
    /// so two concurrent debits can't both read the old balance and overdraw.
    /// </summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
