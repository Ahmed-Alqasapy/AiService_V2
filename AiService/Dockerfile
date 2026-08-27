# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

RUN apt-get update && apt-get install -y python3 python3-pip && rm -rf /var/lib/apt/lists/*

# Copy project file and restore (flexible with wildcards)
COPY *.csproj ./
RUN dotnet restore

# Copy full source and publish (no --no-restore, no hardcoded names)
COPY . ./
RUN dotnet publish -c Release -o out

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/out ./

# Expose port (standard for ASP.NET Core in containers)
EXPOSE 8080

# Entry point (auto-detects the DLL)
ENTRYPOINT ["dotnet", "AiService.dll"]  # Change to exact DLL name if needed (e.g., AiService.dll)