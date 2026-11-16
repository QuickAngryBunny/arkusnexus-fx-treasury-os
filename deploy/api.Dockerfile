# .NET API and Migrator for Cloud Run (hosting on Firebase / Google Cloud).
# Build from the repository root:
#   docker build -f deploy/api.Dockerfile --target api -t fxos-api .
#   docker build -f deploy/api.Dockerfile --target migrator -t fxos-migrator .
# Settings come from environment variables (ConnectionStrings__ControlPlane, TenantServers__default, Secrets__Provider=google,
# Secrets__Google__ProjectId, App__PublicBaseUrl, Email__Firebase__ProjectId, …); credentials from the service account (ADC).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props FxOs.slnx ./
COPY db/ db/
COPY src/ src/
RUN dotnet publish src/FxOs.Api -c Release -o /out/api --nologo \
 && dotnet publish src/FxOs.Migrator -c Release -o /out/migrator --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api ./
# Cloud Run sends traffic to $PORT (8080 by default).
ENV ASPNETCORE_HTTP_PORTS=8080
USER app
ENTRYPOINT ["dotnet", "FxOs.Api.dll"]

# A Cloud Run job run before each release: control plane, then every tenant database.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrator
WORKDIR /app
COPY --from=build /out/migrator ./
USER app
ENTRYPOINT ["dotnet", "FxOs.Migrator.dll"]
CMD ["control"]
