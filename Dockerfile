# syntax=docker/dockerfile:1.7
# Multi-stage build: the SDK image compiles; the runtime image is the chiseled, non-root ASP.NET image
# (no shell, no package manager, minimal attack surface).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Helio.slnx ./
COPY src/Helio.Domain/Helio.Domain.csproj src/Helio.Domain/
COPY src/Helio.Application/Helio.Application.csproj src/Helio.Application/
COPY src/Helio.Infrastructure/Helio.Infrastructure.csproj src/Helio.Infrastructure/
COPY src/Helio.Api/Helio.Api.csproj src/Helio.Api/
RUN dotnet restore src/Helio.Api/Helio.Api.csproj
COPY src/ src/
RUN dotnet publish src/Helio.Api/Helio.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=1 \
    DOTNET_GCDynamicAdaptationMode=1 \
    Storage__ConnectionString="Data Source=/data/helio.db"
COPY --from=build /app .
# The chiseled image runs as the non-root "app" user (uid 1654). /data is the SQLite volume for single-node deployments.
VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "Helio.Api.dll"]
