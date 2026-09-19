# Build the App Portal server.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AppPortal.sln ./
COPY src/AppPortal.Shared/AppPortal.Shared.csproj src/AppPortal.Shared/
COPY src/AppPortal.Server/AppPortal.Server.csproj src/AppPortal.Server/
RUN dotnet restore src/AppPortal.Server/AppPortal.Server.csproj
COPY src/AppPortal.Shared/ src/AppPortal.Shared/
COPY src/AppPortal.Server/ src/AppPortal.Server/
RUN dotnet publish src/AppPortal.Server/AppPortal.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
RUN mkdir -p /app/config /app/data && chown -R app:app /app
USER app
# The data directory holds app-portal.db, which is catalog, devices and install history.
# CatalogPath seeds an empty database on the first start; DevicesPath is read only to import a
# 0.2.x deployment's devices.json once. Neither is read again after that.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Portal__CatalogPath=/app/config/catalog.json \
    Portal__DevicesPath=/app/data/devices.json \
    Portal__DataDirectory=/app/data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s CMD ["dotnet", "AppPortal.Server.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "AppPortal.Server.dll"]
