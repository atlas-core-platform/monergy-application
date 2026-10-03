FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /src

COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["NuGet.Config", "."]
COPY ["contracts/Monergy.Contracts/Monergy.Contracts.csproj", "contracts/Monergy.Contracts/"]
COPY ["contracts/Monergy.Contracts/packages.lock.json", "contracts/Monergy.Contracts/"]
COPY ["shared/platform/Monergy.Platform/Monergy.Platform.csproj", "shared/platform/Monergy.Platform/"]
COPY ["shared/platform/Monergy.Platform/packages.lock.json", "shared/platform/Monergy.Platform/"]
COPY ["services/document-intelligence/Monergy.Services.DocumentIntelligence.csproj", "services/document-intelligence/"]
COPY ["services/document-intelligence/packages.lock.json", "services/document-intelligence/"]
RUN dotnet restore "services/document-intelligence/Monergy.Services.DocumentIntelligence.csproj" --locked-mode

COPY ["contracts/Monergy.Contracts/", "contracts/Monergy.Contracts/"]
COPY ["shared/platform/Monergy.Platform/", "shared/platform/Monergy.Platform/"]
COPY ["services/document-intelligence/", "services/document-intelligence/"]
RUN dotnet publish "services/document-intelligence/Monergy.Services.DocumentIntelligence.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled@sha256:cef1854a63da04795952a34c970591693d1f31c8e8f42ec14892d6198a49774b AS final
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "Monergy.Services.DocumentIntelligence.dll"]
