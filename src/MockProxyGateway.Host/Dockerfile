FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/MockProxyGateway.Host/MockProxyGateway.Host.csproj --locked-mode
RUN dotnet publish src/MockProxyGateway.Host/MockProxyGateway.Host.csproj -c Release --no-restore -o /publish
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MockProxyGateway.Host.dll"]
