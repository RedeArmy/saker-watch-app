# syntax=docker/dockerfile:1.7
# The three hosts of the modular monolith (ADR-0003), one image each, built from the repository root:
#   docker build -f deploy/Dockerfile --target api      -t saker-watch/api .
#   docker build -f deploy/Dockerfile --target worker   -t saker-watch/worker .
#   docker build -f deploy/Dockerfile --target migrator -t saker-watch/migrator .
# The base images are pinned by the digest of their multi-architecture index (linux/amd64 and linux/arm64, the Oracle host) in the FROM lines, where Dependabot
# proposes new digests. Nothing secret is in an image: connection strings and keys arrive as environment variables at run time.

# The build runs on the machine's own architecture and publishes for the target one: no emulation of the compiler.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props SakerWatch.slnx BannedSymbols.txt SonarLint.xml .editorconfig ./
COPY src ./src
# The runtime identifier trims the native assets (the MongoDB driver ships several) to the ones of the target; the output is framework-dependent.
RUN --mount=type=cache,target=/root/.nuget/packages \
    case "$TARGETARCH" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "unsupported architecture $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && for host in Api Worker Migrator; do \
         dotnet publish "src/Hosts/Host.$host/SakerWatch.Hosts.$host.csproj" \
           -c Release -r "$rid" --self-contained false -p:UseAppHost=false -o "/out/$host"; \
       done

# The three hosts run on the same ASP.NET Core base: the worker and the migrator reach the framework through the building blocks.
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca AS base

FROM base AS api
WORKDIR /app
COPY --from=build --chown=1654:1654 /out/Api ./
# 1654 is the `app` user of the Microsoft images. Diagnostics are off: the runtime would open a socket in /tmp for them, and the root file system is read-only.
ENV DOTNET_EnableDiagnostics=0 ASPNETCORE_HTTP_PORTS=8080
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "SakerWatch.Hosts.Api.dll"]

FROM base AS worker
WORKDIR /app
COPY --from=build --chown=1654:1654 /out/Worker ./
ENV DOTNET_EnableDiagnostics=0
USER 1654
ENTRYPOINT ["dotnet", "SakerWatch.Hosts.Worker.dll"]

FROM base AS migrator
WORKDIR /app
COPY --from=build --chown=1654:1654 /out/Migrator ./
ENV DOTNET_EnableDiagnostics=0
USER 1654
ENTRYPOINT ["dotnet", "SakerWatch.Hosts.Migrator.dll"]
