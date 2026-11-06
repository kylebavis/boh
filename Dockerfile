# syntax=docker/dockerfile:1

# ---- build ----------------------------------------------------------------
# Runs on the builder's native architecture and cross-compiles to $TARGETARCH.
# QEMU-emulated .NET builds take 20-40 minutes; this takes the same time as a
# native build regardless of which architecture is being produced.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
ARG TARGETARCH
WORKDIR /src

# Publishing without a RID would copy Magick.NET's native binaries for every
# platform it supports — Windows, macOS, musl, riscv, mips — adding ~270 MB of
# libraries that can never execute here. Targeting one RID keeps only the pair
# this image can actually load.
RUN if [ "$TARGETARCH" = "amd64" ]; then echo linux-musl-x64 > /tmp/rid; \
    else echo "linux-musl-$TARGETARCH" > /tmp/rid; fi

# Restore against the project files alone so the layer survives source edits.
COPY Directory.Build.props ./
COPY src/Boh.Web/Boh.Web.csproj src/Boh.Web/
RUN dotnet restore src/Boh.Web/Boh.Web.csproj -r "$(cat /tmp/rid)"

COPY src/ src/
RUN dotnet publish src/Boh.Web/Boh.Web.csproj \
        -c Release \
        -r "$(cat /tmp/rid)" \
        --self-contained false \
        --no-restore \
        -o /app/publish

# Split boh's own files from its dependencies so a code change ships ~1 MB rather than
# re-sending Magick's native library and the rest in the same layer.
RUN mkdir /app/own \
    && cd /app/publish \
    && mv Boh.Web.* appsettings*.json wwwroot /app/own/ \
    && if [ -e web.config ]; then mv web.config /app/own/; fi

# ---- runtime --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:486297ae006e95ee2ee3112a2c03e3d26e41fbbc64b7a29e28e7a18cc36b12bb AS runtime

# Pinned so an image rebuild cannot silently change importer behavior, and kept in a
# requirements file so Dependabot can see the version and raise the upgrade as a PR.
COPY requirements.txt /tmp/requirements.txt

# libgomp: OpenMP runtime the Magick.NET native library links against.
# ffmpeg:  video probing (ffprobe) and thumbnail extraction.
# python3: runtime for gallery-dl.
# curl:    used by HEALTHCHECK.
# ca-certificates: listed explicitly so rebuilds pick up trust store fixes before the
#          base image does.
RUN apk add --no-cache \
        ca-certificates \
        libgomp \
        ffmpeg \
        python3 \
        curl \
    # A virtualenv rather than a system-wide pip install: Alpine marks its Python
    # environment externally managed (PEP 668).
    && python3 -m venv /opt/gallery-dl \
    && /opt/gallery-dl/bin/pip install --no-cache-dir -r /tmp/requirements.txt \
    # Only needed to install; 16 MB otherwise.
    && /opt/gallery-dl/bin/pip uninstall -y pip \
    && rm /tmp/requirements.txt

ENV PATH="/opt/gallery-dl/bin:${PATH}"

# Created and owned up front so the non-root user can write to a fresh volume. /app stays
# root-owned: it is only read, and chowning it would duplicate every file into a new layer.
# Above the copies so it stays cached across releases.
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data

WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /app/own .

ENV BOH_DATA_PATH=/data \
    ASPNETCORE_URLS=http://+:8080

VOLUME /data
EXPOSE 8080

USER $APP_UID

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Boh.Web.dll"]
