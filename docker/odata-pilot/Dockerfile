# Runtime-only pilot. The harness passes the digest-pinned ASP.NET image from
# the repository's main Dockerfile; there is deliberately no independent pin.
ARG DOTNET_ASPNET_IMAGE
FROM ${DOTNET_ASPNET_IMAGE}
ARG ENTRY_ASSEMBLY=Honua.OData.TestHost
WORKDIR /app
COPY --chown=1654:1654 publish/ ./
USER 1654:1654
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    HOME=/tmp \
    HONUA_PILOT_ASSEMBLY=${ENTRY_ASSEMBLY}
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$HONUA_PILOT_ASSEMBLY.dll\""]
