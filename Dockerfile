FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY Directory.Packages.props inSANE.sln ./
COPY src/InSane.Server/InSane.Server.csproj src/InSane.Server/
RUN dotnet restore src/InSane.Server/InSane.Server.csproj
COPY src/InSane.Server/ src/InSane.Server/
RUN dotnet publish src/InSane.Server/InSane.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
LABEL org.opencontainers.image.licenses="GPL-2.0-or-later" \
      org.opencontainers.image.source="https://github.com/AngelaDMerkel/inSANE"
RUN apt-get update \
    && apt-get install -y --no-install-recommends sane-utils libsane1 libsane-common libusb-1.0-0 curl gosu \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY docker/entrypoint.sh /usr/local/bin/insane-entrypoint
COPY LICENSE THIRD-PARTY-NOTICES /licenses/
RUN mkdir -p /data/state /data/output
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    InSane__Storage__StatePath=/data/state \
    InSane__Storage__OutputPath=/data/output
EXPOSE 8080
VOLUME ["/data/state", "/data/output"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl --fail --silent http://localhost:8080/api/v1/health || exit 1
ENTRYPOINT ["insane-entrypoint"]
CMD ["dotnet", "inSANE.dll"]
