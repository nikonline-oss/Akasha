# ─── Akasha server image ──────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Akasha.Core/Akasha.Core.csproj src/Akasha.Core/
COPY src/Akasha.Server/Akasha.Server.csproj src/Akasha.Server/
RUN dotnet restore src/Akasha.Server/Akasha.Server.csproj
COPY src/ src/
RUN dotnet publish src/Akasha.Server/Akasha.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME /app/data
ENTRYPOINT ["dotnet", "Akasha.Server.dll"]
