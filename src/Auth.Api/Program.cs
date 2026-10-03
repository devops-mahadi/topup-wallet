using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Auth.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Shared.Auth;
using Shared.Observability;

// Keep JWT claim types as-is ("sub" stays "sub"). Without this, the handler
// remaps "sub" to the long ClaimTypes.NameIdentifier URI, so FindFirst("sub") misses.
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<TokenFactory>();

// OpenTelemetry (traces + metrics + logs) over OTLP. No backend bundled — set
// OTEL_EXPORTER_OTLP_ENDPOINT to export. Auth only does HTTP, so no extras.
builder.Services.AddObservability(builder.Configuration, "auth-api");

// Auth.Api validates tokens too (for /auth/me and, crucially, /auth/refresh,
// which must accept a REFRESH token). Same signing key + issuer/audience as the
// other services — one shared secret from config (env), never hardcoded.
var signingKey = builder.Configuration[JwtConventions.SigningKeyConfigPath]
    ?? throw new InvalidOperationException(
        $"Missing JWT signing key at '{JwtConventions.SigningKeyConfigPath}'. Set Jwt__SigningKey.");

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
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        // The bearer scheme is for ACCESS tokens only. Reject a refresh token
        // presented as a bearer (it must go through /auth/refresh instead).
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

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "auth" }));

// --- Login ---
// DEMO credential check: any non-empty username + password succeeds. This is the
// seam where a real user store (DB table) and/or an IdP like Auth0 plugs in —
// out of scope here on purpose. On success we issue an access + refresh token.
app.MapPost("/auth/login", (LoginRequest req, TokenFactory tokens) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "username and password are required" });

    // TODO(real auth): look the user up, verify a hashed password / delegate to Auth0.
    var userId = req.Username.Trim();

    var pair = tokens.Issue(userId);
    return Results.Ok(new
    {
        pair.AccessToken,
        pair.RefreshToken,
        tokenType = "Bearer",
        pair.ExpiresInSeconds
    });
});

// --- Refresh ---
// Exchange a valid REFRESH token for a fresh access + refresh pair. We validate
// the refresh token here (signature + lifetime) and REQUIRE token_type=refresh,
// so an access token can't be replayed against this endpoint. Stateless: no
// stored token to check, so no pre-expiry revocation (named limit).
app.MapPost("/auth/refresh", (RefreshRequest req, TokenFactory tokens) =>
{
    var validation = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = JwtConventions.Issuer,
        ValidateAudience = true,
        ValidAudience = JwtConventions.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };

    try
    {
        var principal = new JwtSecurityTokenHandler()
            .ValidateToken(req.RefreshToken, validation, out _);

        var tokenType = principal.FindFirstValue(JwtConventions.TokenTypeClaim);
        if (tokenType != JwtConventions.RefreshTokenType)
            return Results.BadRequest(new { error = "not a refresh token" });

        var userId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                     ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Results.BadRequest(new { error = "refresh token missing subject" });

        var pair = tokens.Issue(userId);   // rotation: a brand-new refresh token too
        return Results.Ok(new
        {
            pair.AccessToken,
            pair.RefreshToken,
            tokenType = "Bearer",
            pair.ExpiresInSeconds
        });
    }
    catch (SecurityTokenException)
    {
        return Results.Unauthorized();
    }
});

// --- Who am I --- (requires a valid ACCESS token)
app.MapGet("/auth/me", (ClaimsPrincipal user) =>
    Results.Ok(new { userId = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.Identity?.Name }))
   .RequireAuthorization();

app.Run();

record LoginRequest(string Username, string Password);
record RefreshRequest(string RefreshToken);
