FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["SensorReportBot.sln", "./"]
COPY ["src/SensorReportBot.Api/SensorReportBot.Api.csproj", "src/SensorReportBot.Api/"]
COPY ["src/SensorReportBot.Application/SensorReportBot.Application.csproj", "src/SensorReportBot.Application/"]
COPY ["src/SensorReportBot.Domain/SensorReportBot.Domain.csproj", "src/SensorReportBot.Domain/"]
COPY ["src/SensorReportBot.Infrastructure/SensorReportBot.Infrastructure.csproj", "src/SensorReportBot.Infrastructure/"]
COPY ["src/Laya/Laya.csproj", "src/Laya/"]

RUN dotnet restore "src/SensorReportBot.Api/SensorReportBot.Api.csproj"

# Copy source code and build
COPY . .
WORKDIR "/src/src/SensorReportBot.Api"
RUN dotnet build "SensorReportBot.Api.csproj" -c Release -o /app/build
RUN dotnet publish "SensorReportBot.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Install Chromium and system dependencies required for Microsoft Playwright PDF generation
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    chromium \
    libasound2 \
    && rm -rf /var/lib/apt/lists/*

ENV PLAYWRIGHT_BROWSERS_PATH=0 \
    PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 \
    PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH=/usr/bin/chromium

COPY --from=build /app/publish .

EXPOSE 5500

ENV ASPNETCORE_URLS=http://0.0.0.0:5500

ENTRYPOINT ["dotnet", "SensorReportBot.Api.dll"]
