FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS verify
WORKDIR /source
COPY core/ core/
COPY src/ src/
COPY web/ web/
COPY tests/ tests/
RUN dotnet run --project tests/ProxyNodeHub.Tests -c Release
RUN dotnet build src/ProxyNodeHub.csproj -c Release -p:EnableWindowsTargeting=true
