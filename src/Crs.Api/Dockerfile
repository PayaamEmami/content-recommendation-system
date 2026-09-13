# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["Crs.sln", "./"]
COPY ["src/Crs.Api/Crs.Api.csproj", "src/Crs.Api/"]
COPY ["src/Crs.Core/Crs.Core.csproj", "src/Crs.Core/"]
COPY ["src/Crs.Infrastructure/Crs.Infrastructure.csproj", "src/Crs.Infrastructure/"]
COPY ["src/Crs.Recommendation/Crs.Recommendation.csproj", "src/Crs.Recommendation/"]
COPY ["src/Crs.Llm/Crs.Llm.csproj", "src/Crs.Llm/"]

# Restore dependencies
RUN dotnet restore "src/Crs.Api/Crs.Api.csproj"

# Copy all source code
COPY . .

# Build the application
WORKDIR "/src/src/Crs.Api"
RUN dotnet build "Crs.Api.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "Crs.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Create non-root user for security
RUN groupadd -r appuser && useradd -r -g appuser appuser

# aspnet images do not ship curl; install it so the HEALTHCHECK below works.
RUN apt-get update \
  && apt-get install -y --no-install-recommends curl \
  && rm -rf /var/lib/apt/lists/*

# Copy published app
COPY --from=publish /app/publish .

# Set ownership
RUN chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Expose port
EXPOSE 8080

# Health check (ready-tagged checks, including DB)
HEALTHCHECK --interval=30s --timeout=3s --start-period=40s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/health || exit 1

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Crs.Api.dll"]
