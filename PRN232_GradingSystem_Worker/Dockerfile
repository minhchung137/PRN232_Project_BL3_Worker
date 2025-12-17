# Multi-stage build for PRN232_GradingSystem_Worker (background worker)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy toàn bộ solution
COPY . .

# Restore & publish Worker project
WORKDIR /src/PRN232_Project_BL3_Worker/PRN232_GradingSystem_Worker/PRN232_GradingSystem_Worker
RUN dotnet restore \
    && dotnet publish -c Release -o /app/publish --no-restore

# Runtime image (console/worker)
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "PRN232_GradingSystem_Worker.dll"]



