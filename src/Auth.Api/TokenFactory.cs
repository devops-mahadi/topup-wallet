using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Shared.Auth;

namespace Auth.Api;

/// <summary>
/// Mints stateless JWT access + refresh tokens. Nothing is stored: each token
/// carries its own expiry and is validated by signature + lifetime alone.
///
/// Trade-off (named, accepted for this demo): with no server-side store we can't
/// revoke a token before it expires — no real logout-kills-session and no
/// refresh-reuse detection. Production would persist refresh tokens (hashed) to
/// allow rotation + revocation, or delegate issuance to an IdP (Auth0/Entra).
/// </summary>
public class TokenFactory
{
    private readonly SymmetricSecurityKey _key;

    public TokenFactory(IConfiguration config)
    {
        var secret = config[JwtConventions.SigningKeyConfigPath]
            ?? throw new InvalidOperationException(
                $"Missing JWT signing key at '{JwtConventions.SigningKeyConfigPath}'.");
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    public TokenPair Issue(string userId)
    {
        var access = Create(userId, JwtConventions.AccessTokenType, JwtConventions.AccessTokenLifetime);
        var refresh = Create(userId, JwtConventions.RefreshTokenType, JwtConventions.RefreshTokenLifetime);
        return new TokenPair(access, refresh,
            (int)JwtConventions.AccessTokenLifetime.TotalSeconds);
    }

    private string Create(string userId, string tokenType, TimeSpan lifetime)
    {
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtConventions.TokenTypeClaim, tokenType),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var token = new JwtSecurityToken(
            issuer: JwtConventions.Issuer,
            audience: JwtConventions.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public record TokenPair(string AccessToken, string RefreshToken, int ExpiresInSeconds);
