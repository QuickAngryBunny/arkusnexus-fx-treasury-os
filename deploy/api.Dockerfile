# .NET API and Migrator (Cloud Run, Railway or any container host). Build from the repository root:
#   docker build -f deploy/api.Dockerfile -t fxos-api .                       (the API; the default, last stage)
#   docker build -f deploy/api.Dockerfile --target migrator -t fxos-migrator .  (the Migrator alone)
# The API image also carries the Migrator at /app/migrator, so a platform's pre-deploy step can run
#   dotnet /app/migrator/FxOs.Migrator.dll tenants
# Settings come from environment variables (ConnectionStrings__ControlPlane, TenantServers__default, Secrets__Provider,
# App__PublicBaseUrl, Email__Firebase__ProjectId, …).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props FxOs.slnx ./
COPY db/ db/
COPY src/ src/
RUN dotnet publish src/FxOs.Api -c Release -o /out/api --nologo \
 && dotnet publish src/FxOs.Migrator -c Release -o /out/migrator --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrator
WORKDIR /app
COPY --from=build /out/migrator ./
USER app
ENTRYPOINT ["dotnet", "FxOs.Migrator.dll"]
CMD ["control"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api ./
COPY --from=build /out/migrator ./migrator/
ENV ASPNETCORE_HTTP_PORTS=8080
USER app
ENTRYPOINT ["dotnet", "FxOs.Api.dll"]
