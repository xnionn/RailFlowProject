# syntax=docker/dockerfile:1
# RailFlow — .NET 10 Blazor Interactive Server app
#
#   docker build -t railflow .
#   docker run --rm -p 8080:8080 -v railflow-data:/app/data railflow
#
# See docs/DEPLOYMENT.md for configuration and limitations.

# ---------- build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, using only the project files, so this layer is reused
# until a .csproj changes. The test project is not needed for the image.
COPY RailFlow.slnx ./
COPY src/RailFlow.Core/RailFlow.Core.csproj                     src/RailFlow.Core/
COPY src/RailFlow.Infrastructure/RailFlow.Infrastructure.csproj src/RailFlow.Infrastructure/
COPY src/RailFlow.Web/RailFlow.Web.csproj                       src/RailFlow.Web/
RUN dotnet restore src/RailFlow.Web/RailFlow.Web.csproj

# Now the sources, then publish (Data/*.csv is copied to the output by the csproj).
COPY src/ src/
RUN dotnet publish src/RailFlow.Web/RailFlow.Web.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false

# ---------- runtime stage ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Plain HTTP inside the container; TLS is expected to be terminated by a reverse proxy.
# Booking files (bookings.json / bookings.log) and the data-protection keys (data/keys)
# go to /app/data, which is a volume, so sign-ins survive a re-deploy.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Storage__BookingDirectory=/app/data

# Create the booking directory and give it to the non-root "app" user
# (APP_UID is defined by the base image, UID 1654). This must happen before
# VOLUME so a new named volume inherits the ownership.
RUN mkdir -p /app/data \
 && chown -R ${APP_UID}:${APP_UID} /app/data

COPY --from=build /app/publish .

VOLUME ["/app/data"]
EXPOSE 8080

# Run as the built-in non-root "app" user.
USER ${APP_UID}

# No HEALTHCHECK instruction: the aspnet runtime image ships neither curl nor wget.
# Point the platform's HTTP probe at GET /healthz instead.
ENTRYPOINT ["dotnet", "RailFlow.Web.dll"]
