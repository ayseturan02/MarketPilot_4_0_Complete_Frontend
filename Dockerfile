FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/MarketPilot.Web/MarketPilot.Web.csproj src/MarketPilot.Web/
RUN dotnet restore src/MarketPilot.Web/MarketPilot.Web.csproj
COPY src/MarketPilot.Web/ src/MarketPilot.Web/
RUN dotnet publish src/MarketPilot.Web/MarketPilot.Web.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Only the data folder is writable by the runtime user; the application files stay owned by root.
RUN mkdir -p /app/Data && chown -R "$APP_UID":0 /app/Data
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "MarketPilot.Web.dll"]
