# Multi-stage build. Build context is the REPO ROOT (see docker-compose build
# section) so project references (Shared) resolve.

# ---- build stage: full SDK ----
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy only csproj files first and restore — this layer is cached and only
# re-runs when a csproj changes, not on every source edit.
COPY src/Shared/Shared.csproj           src/Shared/
COPY src/Wallet.Api/Wallet.Api.csproj   src/Wallet.Api/
RUN dotnet restore src/Wallet.Api/Wallet.Api.csproj

# Copy the rest of the source and publish a trimmed, release build.
COPY src/ src/
RUN dotnet publish src/Wallet.Api/Wallet.Api.csproj -c Release -o /app/publish --no-restore

# ---- runtime stage: Alpine ASP.NET runtime (small, no SDK) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Alpine ships no ICU, so .NET defaults to globalization-invariant mode, which the
# SQL Server client doesn't support. Install ICU and turn invariant mode off.
RUN apk add --no-cache icu-libs icu-data-full
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=build /app/publish .

# Listen on 8080 inside the container (non-root friendly; map to host in compose).
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Wallet.Api.dll"]
