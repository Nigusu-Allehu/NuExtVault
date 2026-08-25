FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish ./src/NuExtVault.Cli/NuExtVault.Cli.csproj \
    --configuration Release \
    --output /app \
    -p:TreatWarningsAsErrors=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data /https && chown -R $APP_UID:$APP_UID /data
USER $APP_UID
VOLUME ["/data", "/https"]
EXPOSE 8080
ENV ASPNETCORE_Kestrel__Certificates__Default__Path=/https/server.pfx
ENTRYPOINT ["dotnet", "NuExtVault.Cli.dll", "start", "--production", "--url", "https://0.0.0.0:8080", "--storage", "/data", "--api-key-env", "NUEXTVAULT_API_KEY"]
