# syntax=docker/dockerfile:1
# Heimdall Stand-alone-Host — SQLite-only Docker-Image.
#
# 1.0 liefert SQLite-only. Das Walhalla-Backend kehrt als NuGet-Konsument zurück, sobald
# Heimdall.Abstractions gepackt ist (statt der früheren cross-repo ProjectReference).
#
# Build (vom Repo-Root!):  docker build -t heimdall -f host/Heimdall.Host/Dockerfile .
# Run:                     docker run -p 5099:5099 -p 4317:4317 -v ./var/heimdall:/app/var/heimdall heimdall

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Zentrales Directory.Build.props (Version, Nullable-as-Error, SourceLink, …) —
# MSBuild sucht es vom Projektverzeichnis aufwärts, also hier nach /src/ legen.
# global.json bewusst NICHT kopiert: sein SDK-Pin (10.0.400, latestPatch) könnte
# zum sdk:10.0-Image (u. U. 10.0.500+) kollidieren; das Image-SDK ist passend.
COPY Directory.Build.props ./

# Quellen + Beispiel-Dashboard kopieren (Build-Context = Repo-Root).
COPY src/ ./src/
COPY grafana/ ./grafana/
COPY host/Heimdall.Host/ ./host/Heimdall.Host/

# Restore (SQLite-only, keine Cross-Repo-Referenzen mehr).
RUN dotnet restore host/Heimdall.Host/Heimdall.Host.csproj

# Publish: Release, SQLite-only, framework-dependent (aspnet:10.0 als Base).
# Trimming explizit AUS (gRPC-Codegen + Protobuf-Reflection + Razor-Discovery sind trim-unfreundlich).
RUN dotnet publish host/Heimdall.Host/Heimdall.Host.csproj \
    -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# UI/HTTP-OTLP (5099, Http1AndHttp2) + OTLP/gRPC (4317, Http2).
EXPOSE 5099 4317

# Persistenter Telemetrie- + Dashboard-Bestand.
VOLUME /app/var/heimdall

ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "Heimdall.Host.dll"]