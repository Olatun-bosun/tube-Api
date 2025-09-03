# Use the official ASP.NET runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# Install dependencies for yt-dlp
RUN apt-get update && apt-get install -y \
    python3 \
    python3-pip \
    ffmpeg \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Install yt-dlp
RUN pip3 install --no-cache-dir yt-dlp

# Use the SDK image to build the app
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Copy project file and restore dependencies
COPY ["Youtube.csproj", "./"]
RUN dotnet restore "Youtube.csproj"
# Copy everything else and build
COPY . .
WORKDIR "/src"
RUN dotnet build "Youtube.csproj" -c Release -o /app/build

# Publish the app
FROM build AS publish
RUN dotnet publish "Youtube.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Final stage
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV YtDlpPath=yt-dlp
ENV FFmpegPath=ffmpeg
ENV DownloadPath=/tmp/youtube-downloads

# Create download directory
RUN mkdir -p /tmp/youtube-downloads

# Add health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

# Start the application
ENTRYPOINT ["dotnet", "Youtube.dll"]