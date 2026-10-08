FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /source
COPY core/ core/
COPY web/ web/
COPY tests/ tests/
RUN dotnet run --project tests/ProxyNodeHub.Tests -c Release \
    && dotnet publish web/ProxyNodeHub.Web.csproj -c Release --no-restore -o /publish \
    && test -s /publish/known_repos.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
RUN mkdir /data && chown 1654:1654 /data
COPY --from=build /publish/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 DATA_DIR=/data
USER 1654:1654
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:8080/live || exit 1
ENTRYPOINT ["dotnet", "ProxyNodeHub.Web.dll"]
