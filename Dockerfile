# SDK stage: restore + publish the API (tests are not published).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/KeepmarkApi/KeepmarkApi.csproj src/KeepmarkApi/
COPY src/KeepmarkApi.Core/KeepmarkApi.Core.csproj src/KeepmarkApi.Core/
RUN dotnet restore src/KeepmarkApi/KeepmarkApi.csproj
COPY src/ ./src/
RUN dotnet publish src/KeepmarkApi/KeepmarkApi.csproj -c Release -o /app/publish --no-restore

# Runtime stage: ASP.NET Core only (no SDK).
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "KeepmarkApi.dll"]
