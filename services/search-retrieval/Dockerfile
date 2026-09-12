FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /src

COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["NuGet.Config", "."]
COPY ["shared/platform/Monergy.Platform/Monergy.Platform.csproj", "shared/platform/Monergy.Platform/"]
COPY ["shared/platform/Monergy.Platform/packages.lock.json", "shared/platform/Monergy.Platform/"]
COPY ["services/search-retrieval/Monergy.Services.SearchRetrieval.csproj", "services/search-retrieval/"]
COPY ["services/search-retrieval/packages.lock.json", "services/search-retrieval/"]
RUN dotnet restore "services/search-retrieval/Monergy.Services.SearchRetrieval.csproj" --locked-mode

COPY ["shared/platform/Monergy.Platform/", "shared/platform/Monergy.Platform/"]
COPY ["services/search-retrieval/", "services/search-retrieval/"]
RUN dotnet publish "services/search-retrieval/Monergy.Services.SearchRetrieval.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02 AS final
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "Monergy.Services.SearchRetrieval.dll"]
