# Builds and runs Marketlens.Api. Context: services/api.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer is cached until a project file changes.
COPY .editorconfig global.json Directory.Build.props Marketlens.slnx ./
COPY Marketlens.Api/Marketlens.Api.csproj Marketlens.Api/
RUN dotnet restore Marketlens.Api/Marketlens.Api.csproj

COPY Marketlens.Api/ Marketlens.Api/
RUN dotnet publish Marketlens.Api/Marketlens.Api.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# The base image's unprivileged user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Marketlens.Api.dll"]
