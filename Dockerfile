# The Roslyn engine needs a real MSBuild toolset available at RUNTIME, not just at build time —
# Microsoft.Build.Locator.RegisterDefaults() (src/Aip.Engines.Roslyn/RoslynEngineModule.cs) scans the
# running machine for an installed .NET SDK every time it analyzes a target repo's .csproj/.sln files. So,
# unlike a typical ASP.NET Core container, the final image must stay on the full SDK base image rather than
# the slimmer aspnet/runtime image — dropping to a runtime-only image would silently break C#/.NET analysis.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Aip.Host/Aip.Host.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /app

# GitRepositorySource (src/Aip.Infrastructure/Sourcing.cs) shells out to the git CLI directly for every
# clone/fetch/diff — not present in the base SDK image by default.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Aip.slnx marks the "solution root" every entry point resolves config relative to (PlatformRunner.
# FindSolutionRoot) — placed alongside the published binaries so it's found with zero directory walk-up.
# apps.yml is the application registry `serve` mode reads on every /run call; appsettings.json is the
# committed structure/defaults file (no secrets — see its own comment). Real secrets and the SQL/Storage/AI
# connection strings come from environment variables at deploy time, never baked into the image.
COPY Aip.slnx apps.yml appsettings.json ./

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Aip.Host.dll", "serve", "--config", "apps.yml"]
