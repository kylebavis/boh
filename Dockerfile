# syntax=docker/dockerfile:1

# ---- build ----------------------------------------------------------------
# Runs on the builder's native architecture and cross-compiles to $TARGETARCH.
# QEMU-emulated .NET builds take 20-40 minutes; this takes the same time as a
# native build regardless of which architecture is being produced.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Publishing without a RID would copy Magick.NET's native binaries for every
# platform it supports — Windows, macOS, musl, riscv, mips — adding ~270 MB of
# libraries that can never execute here. Targeting one RID keeps only the pair
# this image can actually load.
RUN if [ "$TARGETARCH" = "amd64" ]; then echo linux-x64 > /tmp/rid; \
    else echo "linux-$TARGETARCH" > /tmp/rid; fi

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
# Debian rather than Alpine: this image also carries Python (gallery-dl) and the
# Magick.NET native libraries, and glibc avoids a class of musl packaging problems.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Pinned so an image rebuild cannot silently change importer behaviour, and kept in a
# requirements file so Dependabot can see the version and raise the upgrade as a PR.
COPY requirements.txt /tmp/requirements.txt

# libgomp1: OpenMP runtime the Magick.NET native library links against.
# ffmpeg:   video probing (ffprobe) and thumbnail extraction.
# python3:  runtime for gallery-dl.
# curl:     used by HEALTHCHECK.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libgomp1 \
        ffmpeg \
        python3 \
        python3-venv \
        curl \
    # A virtualenv rather than a system-wide pip install: Debian marks its Python
    # environment externally managed (PEP 668), and --break-system-packages is exactly
    # the kind of override that later bites during a base image upgrade.
    && python3 -m venv /opt/gallery-dl \
    && /opt/gallery-dl/bin/pip install --no-cache-dir -r /tmp/requirements.txt \
    # Only needed to install; 16 MB otherwise.
    && /opt/gallery-dl/bin/pip uninstall -y pip \
    && apt-get purge -y --auto-remove python3-venv \
    && rm -rf /var/lib/apt/lists/* /tmp/requirements.txt \
    # ffmpeg depends on libavdevice, which links the GL stack, which drags in Mesa's
    # software renderer and LLVM — about 180 MB of GPU driver in a container that only
    # ever decodes one frame to a file. The packages cannot be purged without taking
    # ffmpeg with them, but the driver backend is dlopened lazily when a GL context is
    # created, which never happens here. libGL itself is left in place so ffmpeg still
    # resolves its NEEDED entries at load time. Verified: ffprobe and frame extraction
    # both work with these removed.
    && rm -f /usr/lib/*-linux-gnu/libLLVM.so* \
             /usr/lib/*-linux-gnu/libgallium*.so \
    && rm -rf /usr/lib/*-linux-gnu/dri

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
