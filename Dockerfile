# syntax=docker/dockerfile:1

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY fmo-audit-service.csproj ./
RUN dotnet restore fmo-audit-service.csproj

COPY . .

# Docker uses framework-dependent deployment; the csproj's single-file/self-contained
# settings are meant for the OTA-distributed binaries, not the container image.
RUN dotnet publish fmo-audit-service.csproj \
    -c Release \
    -o /out \
    --no-restore \
    -p:PublishSingleFile=false \
    -p:SelfContained=false \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

COPY --from=build --chown=app:app /out/ ./

ENV HOME=/home/app \
    DOTNET_RUNNING_IN_CONTAINER=true \
    EMQX_MONITOR_PORT=9527 \
    EMQX_MONITOR_DB=/data/fmo-audit-service.db

RUN mkdir -p /data && chown -R app:app /data

USER app

VOLUME ["/data"]

EXPOSE 9527

ENTRYPOINT ["dotnet", "fmo-audit-service.dll"]
