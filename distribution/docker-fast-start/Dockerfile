# syntax=docker/dockerfile:1
#
# Sonarr with bundle fast-start, built from this fork.
#
# Only Sonarr.Core.dll and Sonarr.Common.dll differ from stock Sonarr 4.0.20.3014, so they are built here and laid over the
# official linuxserver.io image of exactly the same version (frontend, runtime and every other assembly come from it).
#
# Build from the repository root:
#   docker build -f distribution/docker-fast-start/Dockerfile -t sonarr-fast-start:4.0.20.3014 .

ARG SONARR_VERSION=4.0.20.3014
ARG BASE_IMAGE=lscr.io/linuxserver/sonarr:4.0.20.3014-ls327

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
ARG SONARR_VERSION

WORKDIR /src

COPY global.json .editorconfig ./
COPY Logo ./Logo
COPY src ./src

# Same version stamping as build.sh, so the assembly version matches the base image (4.0.20.3014)
RUN sed -i "s/<AssemblyVersion>[0-9.*]\+<\/AssemblyVersion>/<AssemblyVersion>${SONARR_VERSION}<\/AssemblyVersion>/g" src/Directory.Build.props && \
    sed -i "s/<AssemblyConfiguration>[\$()A-Za-z-]\+<\/AssemblyConfiguration>/<AssemblyConfiguration>main<\/AssemblyConfiguration>/g" src/Directory.Build.props && \
    dotnet build src/NzbDrone.Core/Sonarr.Core.csproj -c Release -p:Platform=Posix -p:SolutionDir=/src/src/ && \
    mkdir /out && \
    cp _output/net6.0/Sonarr.Core.dll _output/net6.0/Sonarr.Core.pdb _output/net6.0/Sonarr.Common.dll _output/net6.0/Sonarr.Common.pdb /out/

FROM ${BASE_IMAGE}

LABEL org.opencontainers.image.title="Sonarr (bundle fast-start)" \
      org.opencontainers.image.description="Sonarr 4.0.20.3014 with bundle-first search and partial qBittorrent bundle downloads" \
      org.opencontainers.image.source="https://github.com/Niaxor/Sonarr"

COPY --from=build /out/ /app/sonarr/bin/
