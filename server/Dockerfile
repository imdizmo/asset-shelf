# ShelfServer as a container (amd64 and arm64). Build from the repository root:
#   docker build -f server/Dockerfile -t shelf-server .
# Run it beside Tailscale's own container: see server/docker/README.md.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json ./
COPY server/ShelfServer/ server/ShelfServer/
RUN rid=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
 && dotnet publish server/ShelfServer -c Release -r "$rid" --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=none -o /out

# no shell, no package manager; runs as a non-root user (uid 1654)
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
COPY --from=build /out/ShelfServer /app/ShelfServer
USER app
VOLUME ["/data"]
EXPOSE 8781
ENV DOTNET_GCHeapHardLimit=0x10000000
ENTRYPOINT ["/app/ShelfServer"]
CMD ["--listen", "tailscale:8781", "--store", "/data", "--team", "/team/team.json"]
