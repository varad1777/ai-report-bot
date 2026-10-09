# Multi-stage Dockerfile for .NET 9 Web API
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files first to cache nuget restores
COPY ["SensorReportBot.sln", "./"]
COPY ["src/SensorReportBot.Domain/SensorReportBot.Domain.csproj", "src/SensorReportBot.Domain/"]
COPY ["src/SensorReportBot.Application/SensorReportBot.Application.csproj", "src/SensorReportBot.Application/"]
COPY ["src/SensorReportBot.Infrastructure/SensorReportBot.Infrastructure.csproj", "src/SensorReportBot.Infrastructure/"]
COPY ["src/SensorReportBot.Api/SensorReportBot.Api.csproj", "src/SensorReportBot.Api/"]

RUN dotnet restore "SensorReportBot.sln"

# Copy full source tree and publish
COPY . .
WORKDIR "/src/src/SensorReportBot.Api"
RUN dotnet publish "SensorReportBot.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Install fontconfig and fonts required by QuestPDF (SkiaSharp) on Linux
RUN apt-get update && apt-get install -y --no-install-recommends \
    libfontconfig1 \
    libfreetype6 \
    fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

# Copy build artifacts
COPY --from=build /app/publish .

# Create storage directory for generated PDF reports
RUN mkdir -p /app/storage/reports

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Development
EXPOSE 5000

ENTRYPOINT ["dotnet", "SensorReportBot.Api.dll"]
