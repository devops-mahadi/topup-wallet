namespace Shared.Auth;

/// <summary>
/// Shared JWT conventions so both services validate tokens the same way.
/// The signing KEY is NOT here — it's a secret read from configuration at
/// runtime (never committed). These are just the non-secret agreed values.
///
/// This is a DEMO setup using a symmetric key and a self-issued token (TopUp.Api
/// exposes a /token endpoint). In production you'd delegate to a real identity
/// provider (OIDC: Entra ID, Keycloak, Auth0) with asymmetric keys and key
/// rotation via a JWKS endpoint — the validation wiring stays the same shape.
/// </summary>
public static class JwtConventions
{
    public const string Issuer = "topup-wallet-auth";
    public const string Audience = "topup-wallet-api";

    /// <summary>Claim carrying the user id (also the JWT standard "sub").</summary>
    public const string UserIdClaim = "sub";

    /// <summary>Config key holding the symmetric signing secret.</summary>
    public const string SigningKeyConfigPath = "Jwt:SigningKey";

    /// <summary>Token-type claim, so a refresh token can't be used as an access
    /// token (and vice-versa). Values below.</summary>
    public const string TokenTypeClaim = "token_type";
    public const string AccessTokenType = "access";
    public const string RefreshTokenType = "refresh";

    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);
}
