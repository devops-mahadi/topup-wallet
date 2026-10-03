# Multi-stage build. Build context is the REPO ROOT (see docker-compose build
# section) so project references (Shared) resolve.

# ---- build stage: full SDK ----
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy csproj files first and restore — cached layer, re-runs only on csproj change.
COPY src/Shared/Shared.csproj         src/Shared/
COPY src/TopUp.Api/TopUp.Api.csproj   src/TopUp.Api/
RUN dotnet restore src/TopUp.Api/TopUp.Api.csproj

# Copy the rest of the source and publish a release build.
COPY src/ src/
RUN dotnet publish src/TopUp.Api/TopUp.Api.csproj -c Release -o /app/publish --no-restore

# ---- runtime stage: Alpine ASP.NET runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Alpine ships no ICU, so .NET defaults to globalization-invariant mode. Install
# ICU and disable invariant mode for full culture support.
RUN apk add --no-cache icu-libs icu-data-full
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "TopUp.Api.dll"]
