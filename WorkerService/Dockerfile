# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY WorkerService/WorkerService.csproj WorkerService/
RUN dotnet restore WorkerService/WorkerService.csproj

# Copy everything else and build
COPY WorkerService/ WorkerService/
WORKDIR /src/WorkerService
RUN dotnet build -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Copy appsettings.json from build stage if not already in publish
COPY --from=build /src/WorkerService/appsettings.json ./appsettings.json
ENTRYPOINT ["dotnet", "WorkerService.dll"]

