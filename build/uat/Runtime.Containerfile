FROM node:24.21.0-bookworm-slim@sha256:d6aa754f16b3197301076f047b5def2f02ea1dbbc2ca920407d46d7ec7f87b20 AS frontend
WORKDIR /source
COPY . .
RUN npm install --global pnpm@12.4.1 && pnpm install --frozen-lockfile
RUN node build/uat/restore-source.mjs build/uat/dependencies /access-management
ENV VITE_MONERGY_LOCAL_UAT=true
RUN pnpm --filter @monergy/customer-web build

FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /source
COPY . .
COPY --from=frontend /access-management /access-management
ENV DOTNET_PROCESSOR_COUNT=2 DOTNET_EnableDiagnostics=0
RUN for project in services/*/*.csproj; do service=$(basename "$(dirname "$project")"); dotnet publish "$project" -c Release -p:RestoreLockedMode=true -o "/out/$service" -m:1 -p:UseSharedCompilation=false || exit 1; done
RUN dotnet publish build/local/Monergy.LocalAccessAuditHost -c Release -p:RestoreLockedMode=true -o /out/audit -m:1 -p:UseSharedCompilation=false && dotnet publish build/uat/Monergy.UatGateway -c Release -p:RestoreLockedMode=true -o /out/workspace -m:1 -p:UseSharedCompilation=false && dotnet publish build/Monergy.DatabaseMigrator -c Release -p:RestoreLockedMode=true -o /out/bootstrap -m:1 -p:UseSharedCompilation=false
RUN dotnet publish /access-management/src/Monergy.AccessManagement -c Release -p:RestoreLockedMode=true -o /out/access-management -m:1 -p:UseSharedCompilation=false && dotnet publish /access-management/tools/Monergy.AccessManagement.Migrations -c Release -p:RestoreLockedMode=true -o /out/am-migrate -m:1 -p:UseSharedCompilation=false && dotnet publish /access-management/tools/Monergy.AccessManagement.DeliveryReference -c Release -p:RestoreLockedMode=true -o /out/delivery -m:1 -p:UseSharedCompilation=false
# Copy the prepared directory as a child, preserving its own mode. COPY of an
# empty directory onto a destination does not carry the source directory mode.
RUN mkdir -p /runtime-data/monergy-onboarding && chmod 1777 /runtime-data/monergy-onboarding

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c
COPY --from=build /out /app
COPY --from=build /source/services/customer-identity/migrations /source/services/customer-identity/migrations
COPY --from=build /source/services/audit/migrations /source/services/audit/migrations
COPY --from=build /source/services/consent/migrations /source/services/consent/migrations
COPY --from=build /runtime-data/ /var/lib/
COPY --from=frontend /source/apps/customer-web/dist /app/workspace/wwwroot
ENV DOTNET_PROCESSOR_COUNT=2 DOTNET_EnableDiagnostics=0 DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development
# Fail the image build if an arbitrary non-root UID cannot perform the actual
# registry create/read/atomic-replace operations. No database or profile is used.
USER 1001
RUN ["dotnet", "/app/bootstrap/Monergy.DatabaseMigrator.dll", "--verify-local-uat-storage", "/var/lib/monergy-onboarding"]
USER 1654
ENTRYPOINT ["dotnet"]
