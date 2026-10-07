# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# Stage 1: Build using the .NET 8 SDK Alpine image
FROM mcr.microsoft.com/dotnet/sdk:8.0.403-alpine3.20 AS build
WORKDIR /src

# Leverage Docker layer caching by restoring packages first
COPY ["MpaDataExplorer.csproj", "./"]
RUN dotnet restore "MpaDataExplorer.csproj"

# Copy remaining source code and publish a Release build
COPY . .
RUN dotnet publish "MpaDataExplorer.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime using the minimal ASP.NET Core Alpine image (~105MB)
FROM mcr.microsoft.com/dotnet/aspnet:8.0.10-alpine3.20 AS runtime
WORKDIR /app

# Run as the built-in non-root user 'app' for container security
USER app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV OBIS_API_BASE_URL=https://api.obis.org/v3/

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MpaDataExplorer.dll"]