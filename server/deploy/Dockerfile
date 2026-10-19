# Ombra team server image: ASP.NET Core 10 server + ombra-admin + ombra-core (Rust, C ABI) + age.
# Build context: server/   (docker build -f deploy/Dockerfile -t ombra-server:<version> .)

FROM rust:1-bookworm AS native
WORKDIR /src/native/ombra-core
COPY native/ombra-core/Cargo.toml native/ombra-core/Cargo.lock ./
COPY native/ombra-core/src ./src
RUN cargo build --release --locked --lib \
 && cp target/release/libombra_core.so /libombra_core.so

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY migrations ./migrations
COPY src ./src
# The Interop project picks the native library from OMBRA_CORE_NATIVE_PATH at run time; the build does not need it.
RUN dotnet publish src/Ombra.Server/Ombra.Server.csproj -c Release -o /out/server \
 && dotnet publish src/Ombra.Server.Cli/Ombra.Server.Cli.csproj -c Release -o /out/cli

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends age libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/server /app
COPY --from=build /out/cli /opt/ombra-admin
COPY --from=native /libombra_core.so /usr/local/lib/ombra/libombra_core.so
RUN printf '#!/bin/sh\nexec dotnet /opt/ombra-admin/ombra-admin.dll "$@"\n' > /usr/local/bin/ombra-admin \
 && chmod 0755 /usr/local/bin/ombra-admin \
 && mkdir -p /var/lib/ombra /backups \
 && chown -R app:app /var/lib/ombra /backups
ENV OMBRA_CORE_NATIVE_PATH=/usr/local/lib/ombra/libombra_core.so \
    OMBRA_ENV_FILE=/etc/ombra/server.env \
    DOTNET_gcServer=0
USER app
EXPOSE 8443
ENTRYPOINT ["dotnet", "/app/Ombra.Server.dll"]
