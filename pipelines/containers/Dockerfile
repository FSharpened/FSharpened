# Linux distribution channel for CI and agent sandboxes (multi-arch: linux/amd64, linux/arm64).
# Not a platform preference; see docs/platform/containers.md. The repo must remain operable without it.
FROM mcr.microsoft.com/dotnet/sdk:9.0
RUN dotnet tool install -g fantomas \
 && dotnet tool install -g fsautocomplete
ENV PATH="${PATH}:/root/.dotnet/tools" \
    DOTNET_RUNNING_IN_CONTAINER=true
WORKDIR /src
# TODO: add OTLP collector, sign image and attach SBOM via FSharpened.Security.
