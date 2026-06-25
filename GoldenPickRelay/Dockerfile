# multi-stage: build with the SDK, run on the smaller aspnet runtime image.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY GoldenPickRelay.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
# bind to the port Fly forwards to (see fly.toml internal_port). Fly terminates TLS
# at the edge, so clients use wss:// and Fly proxies plain http to us here.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "GoldenPickRelay.dll"]
