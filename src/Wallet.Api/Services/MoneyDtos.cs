namespace Wallet.Api.Services;

/// <summary>A debit or credit request. IdempotencyKey makes it safe to retry.</summary>
public record MoneyRequest(Guid AccountId, decimal Amount, string IdempotencyKey);

/// <summary>Result of a money operation — the resulting balance + the ledger entry id.</summary>
public record MoneyResult(Guid TransactionId, decimal Balance, bool WasReplay);

/// <summary>Thrown when a debit would take the balance below zero.</summary>
public class InsufficientFundsException : Exception
{
    public InsufficientFundsException() : base("Insufficient funds.") { }
}
