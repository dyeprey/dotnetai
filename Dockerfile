# syntax=docker/dockerfile:1

# =============================================================================
# STAGE 1 — build
# =============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project file ALONE and restore before copying the source. Docker caches each
# layer against the files it read, so a source-only edit reuses the restore layer and skips
# a full NuGet download. Copy everything first and every one-character change re-restores.
COPY dotnetai.Api/dotnetai.Api.csproj dotnetai.Api/
RUN dotnet restore dotnetai.Api/dotnetai.Api.csproj

COPY . .

RUN dotnet publish dotnetai.Api/dotnetai.Api.csproj \
    --no-restore \
    --configuration Release \
    --output /app/publish

# =============================================================================
# STAGE 2 — the migration bundle
# =============================================================================
#
# `dotnet ef migrations bundle` compiles Migrations/ into a standalone executable that
# applies them and exits. It is the deployment-friendly half of `dotnet ef database update`:
# no SDK, no source tree, and no EF tooling needed on the server — just the binary and a
# connection string.
#
# Built in its own stage because it needs the SDK and the dotnet-ef tool, neither of which
# has any business in the runtime image. Only the finished executable is copied forward.
FROM build AS migrations

# The tool manifest at the repo root pins dotnet-ef to 10.0.11, matching the EF packages in
# the csproj. Restoring it rather than installing a floating "latest" keeps the tool and the
# runtime provider on the same version — a mismatch produces model-snapshot errors that read
# as if your migrations are corrupt.
COPY dotnet-tools.json ./.config/dotnet-tools.json
RUN dotnet tool restore

# EF builds the app's host at design time to discover AppDbContext. Program.cs reads the Jwt
# section during service registration, so the section has to be present and well-formed even
# though nothing here signs a token. These placeholders exist only inside this build stage —
# the runtime image is a different stage and never sees them. The real values arrive as
# environment variables at container start.
ENV Jwt__Key="build-time-placeholder-value-that-is-only-here-to-satisfy-startup" \
    OpenAI__ApiKey="build-time-placeholder"

RUN dotnet ef migrations bundle \
    --project dotnetai.Api/dotnetai.Api.csproj \
    --configuration Release \
    --no-build \
    --force \
    --output /app/efbundle

# =============================================================================
# STAGE 3 — runtime
# =============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is here purely for the compose healthcheck. The aspnet image ships neither curl nor
# wget, and a healthcheck is what lets `depends_on: condition: service_healthy` mean
# "actually answering requests" rather than "the process exists".
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build      /app/publish  ./
COPY --from=migrations /app/efbundle ./efbundle

# The SQLite file lives on a mounted volume, NOT in the image — an image layer is recreated
# on every deploy, which would silently discard every user account on each release.
#
# Creating the directory and chowning it here is load-bearing. Docker seeds a NEW named
# volume from whatever is at the mount point in the image, ownership included. Skip this and
# the volume arrives owned by root, the non-root user below cannot write, and SQLite fails
# with "attempt to write a readonly database" at the first registration.
RUN mkdir -p /data && chown app:app /data
VOLUME ["/data"]

# ASP.NET Core's Data Protection keyring defaults to $HOME/.aspnet/DataProtection-Keys, which
# is INSIDE the container and therefore destroyed on every redeploy. This app signs its JWTs
# with an explicit HMAC key rather than with Data Protection, so nothing breaks today — but
# the moment anything uses it (antiforgery tokens, cookie auth, TempData) a rolling deploy
# would invalidate every one of those payloads, and the failure would look random.
#
# Same ownership reasoning as /data: the directory must exist and be owned by `app` before the
# volume is mounted over it, or the new volume arrives owned by root and unwritable.
RUN mkdir -p /home/app/.aspnet && chown -R app:app /home/app/.aspnet

# The .NET images ship a non-root `app` user (uid 1654). Use it: a container process that
# never needs root should never have it, so a remote-code-execution bug in a dependency
# lands as an unprivileged user instead of as root inside the container.
USER app

# The base image defaults to 8080 and Production; both are restated because the port is
# referenced by name in the Caddyfile and the compose healthcheck, and a reader should not
# have to know the base image's defaults to follow the wiring.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "dotnetai.Api.dll"]
