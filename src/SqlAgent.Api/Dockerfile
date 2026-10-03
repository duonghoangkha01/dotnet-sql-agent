# The API image. The build context is the repository root:
#   docker build -f src/SqlAgent.Api/Dockerfile .
# Base images are pinned by digest like the rest of the stack, so a moved tag can't change behavior silently.

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src

# Project files first, so the restore layer is reused until a dependency changes.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/SqlAgent.Core/SqlAgent.Core.csproj src/SqlAgent.Core/
COPY src/SqlAgent.Api/SqlAgent.Api.csproj src/SqlAgent.Api/
RUN dotnet restore src/SqlAgent.Api/SqlAgent.Api.csproj

COPY src/SqlAgent.Core src/SqlAgent.Core
COPY src/SqlAgent.Api src/SqlAgent.Api
RUN dotnet publish src/SqlAgent.Api/SqlAgent.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
WORKDIR /app
COPY --from=build /app .
# The image's built-in non-root user.
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SqlAgent.Api.dll"]
