# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Docugen.Core/Docugen.Core.csproj src/Docugen.Core/
COPY src/Docugen.Infrastructure/Docugen.Infrastructure.csproj src/Docugen.Infrastructure/
COPY src/Docugen.Api/Docugen.Api.csproj src/Docugen.Api/
RUN dotnet restore src/Docugen.Api/Docugen.Api.csproj
COPY src/ src/
RUN dotnet publish src/Docugen.Api/Docugen.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_HTTP_PORTS=8080 \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
WORKDIR /app
COPY --from=build /app ./

# Chromium (matching the Playwright package), its system libraries and fonts for PDF rendering.
RUN apt-get update \
    && apt-get install -y --no-install-recommends fonts-liberation fonts-noto-core \
    && dotnet Docugen.Api.dll install-browsers --with-deps \
    && rm -rf /var/lib/apt/lists/*

# Report definitions are baked in; mount a volume over /app/reports to manage them separately.
COPY reports/ ./reports/

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Docugen.Api.dll"]
