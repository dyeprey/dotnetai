# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY dotnetai.Api/dotnetai.Api.csproj dotnetai.Api/
RUN dotnet restore dotnetai.Api/dotnetai.Api.csproj

COPY . .

RUN dotnet publish dotnetai.Api/dotnetai.Api.csproj \
    --no-restore \
    --configuration Release \
    --output /app/publish

FROM build AS migrations

COPY dotnet-tools.json ./.config/dotnet-tools.json
RUN dotnet tool restore

ENV Jwt__Key="build-time-placeholder-value-that-is-only-here-to-satisfy-startup" \
    OpenAI__ApiKey="build-time-placeholder"

RUN dotnet ef migrations bundle \
    --project dotnetai.Api/dotnetai.Api.csproj \
    --configuration Release \
    --no-build \
    --force \
    --output /app/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build      /app/publish  ./
COPY --from=migrations /app/efbundle ./efbundle

RUN mkdir -p /data && chown app:app /data
VOLUME ["/data"]

RUN mkdir -p /home/app/.aspnet && chown -R app:app /home/app/.aspnet

USER app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "dotnetai.Api.dll"]
