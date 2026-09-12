FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /src

COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["NuGet.Config", "."]
COPY ["shared/platform/Monergy.Platform/Monergy.Platform.csproj", "shared/platform/Monergy.Platform/"]
COPY ["shared/platform/Monergy.Platform/packages.lock.json", "shared/platform/Monergy.Platform/"]
COPY ["services/reporting/Monergy.Services.Reporting.csproj", "services/reporting/"]
COPY ["services/reporting/packages.lock.json", "services/reporting/"]
RUN dotnet restore "services/reporting/Monergy.Services.Reporting.csproj" --locked-mode

COPY ["shared/platform/Monergy.Platform/", "shared/platform/Monergy.Platform/"]
COPY ["services/reporting/", "services/reporting/"]
RUN dotnet publish "services/reporting/Monergy.Services.Reporting.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0.12-noble-chiseled@sha256:f828e6ef5140bb1b2f5b681535f197a243d91ce0ab3651399879a0c567dd1de7 AS final
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "Monergy.Services.Reporting.dll"]
