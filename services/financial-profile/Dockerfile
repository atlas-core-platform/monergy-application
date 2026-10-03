FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /src

COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["NuGet.Config", "."]
COPY ["contracts/Monergy.Contracts/Monergy.Contracts.csproj", "contracts/Monergy.Contracts/"]
COPY ["contracts/Monergy.Contracts/packages.lock.json", "contracts/Monergy.Contracts/"]
COPY ["shared/platform/Monergy.Platform/Monergy.Platform.csproj", "shared/platform/Monergy.Platform/"]
COPY ["shared/platform/Monergy.Platform/packages.lock.json", "shared/platform/Monergy.Platform/"]
COPY ["services/financial-profile/Monergy.Services.FinancialProfile.csproj", "services/financial-profile/"]
COPY ["services/financial-profile/packages.lock.json", "services/financial-profile/"]
RUN dotnet restore "services/financial-profile/Monergy.Services.FinancialProfile.csproj" --locked-mode

COPY ["contracts/Monergy.Contracts/", "contracts/Monergy.Contracts/"]
COPY ["shared/platform/Monergy.Platform/", "shared/platform/Monergy.Platform/"]
COPY ["services/financial-profile/", "services/financial-profile/"]
RUN dotnet publish "services/financial-profile/Monergy.Services.FinancialProfile.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "Monergy.Services.FinancialProfile.dll"]
