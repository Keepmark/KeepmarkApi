# SDK stage: restore + publish the API (tests are not published).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/KeepmarkApi/KeepmarkApi.csproj src/KeepmarkApi/
COPY src/KeepmarkApi.Core/KeepmarkApi.Core.csproj src/KeepmarkApi.Core/
RUN dotnet restore src/KeepmarkApi/KeepmarkApi.csproj
COPY src/ ./src/
# Single step (implicit restore): immune to cross-layer package-cache issues.
RUN dotnet publish src/KeepmarkApi/KeepmarkApi.csproj -c Release -o /app/publish

# Runtime stage: ASP.NET Core only (no SDK).
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl powers the container HEALTHCHECK below (not otherwise used).
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -f http://localhost:8080/info || exit 1
ENTRYPOINT ["dotnet", "KeepmarkApi.dll"]
