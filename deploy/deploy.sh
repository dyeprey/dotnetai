#!/usr/bin/env bash
#
# Deploy the API on the droplet from a registry image.
#
#   ./deploy/deploy.sh                        # deploy whatever :latest points at
#   IMAGE_TAG=sha-abc123 ./deploy/deploy.sh   # deploy (or roll back to) one exact commit
#   SKIP_GIT_PULL=1 ./deploy/deploy.sh        # deploy without updating the working tree
#
# This never builds. The image comes from GitHub Actions, which is the only place it is
# built and the only place the tests that gate it have run.

set -Eeuo pipefail

# -e alone is not enough. Without pipefail, `docker compose up | tee` reports the exit code
# of tee — so a failed deploy looks like a successful one. Without -u, a typo'd variable
# expands to empty and the script cheerfully carries on with a wrong value.

cd "$(dirname "${BASH_SOURCE[0]}")/.."
REPO_DIR="$PWD"
BACKUP_DIR="${BACKUP_DIR:-$REPO_DIR/backups}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-120}"

# Read a key out of .env the way Compose would.
#
# THIS MATTERS MORE THAN IT LOOKS. Compose reads .env by itself; bash does not. Without
# this, IMAGE_REF below would fall back to the hard-coded default whenever IMAGE lives in
# .env rather than the shell - and IMAGE_REF is what the rollback re-tags. The rollback
# would then tag an image name nothing is configured to run, and the recovery would fail
# at the exact moment it is needed. Shell environment still wins, matching Compose.
env_value() { grep -E "^$1=" .env 2>/dev/null | tail -1 | cut -d= -f2- | tr -d "\"'"; }

log()  { printf '\033[1;34m▸\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m✓\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# PREFLIGHT — fail before touching anything that is currently serving
# ---------------------------------------------------------------------------
log "Preflight"

command -v docker >/dev/null || die "docker is not installed"
docker compose version >/dev/null 2>&1 || die "the docker compose plugin is not installed"
[[ -f docker-compose.yml ]] || die "no docker-compose.yml in $REPO_DIR"
[[ -f .env ]] || die ".env is missing. Copy .env.example to .env and fill it in."

# Compose refuses to start without these anyway, but it does so AFTER stopping the old
# containers. Checking here means a missing secret is a no-op instead of an outage.
for required in JWT_KEY OPENAI_API_KEY WEB_ORIGIN; do
    grep -qE "^${required}=.+" .env || die "$required is empty in .env"
done
ok "docker, compose file and .env all present"

IMAGE="${IMAGE:-$(env_value IMAGE)}"
IMAGE_TAG="${IMAGE_TAG:-$(env_value IMAGE_TAG)}"
IMAGE_REF="${IMAGE:-ghcr.io/dyeprey/dotnetai-api}:${IMAGE_TAG:-latest}"
ok "target image $IMAGE_REF"

# ---------------------------------------------------------------------------
# UPDATE THE WORKING TREE
# ---------------------------------------------------------------------------
# The image comes from the registry, but docker-compose.yml, the Caddyfile and this script
# come from git — so a deploy that changes any of those needs the tree updated too.
if [[ "${SKIP_GIT_PULL:-0}" != "1" && -d .git ]]; then
    log "Updating working tree"
    if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
        warn "working tree has local modifications — skipping git pull"
        git status --short --untracked-files=no | sed 's/^/    /'
    else
        # --ff-only so a diverged branch stops the deploy instead of silently merging.
        git pull --ff-only
        ok "now at $(git rev-parse --short HEAD)"
    fi
fi

# ---------------------------------------------------------------------------
# RECORD THE CURRENT STATE, so a failed deploy can be undone
# ---------------------------------------------------------------------------
PREVIOUS_IMAGE="$(docker inspect --format '{{.Image}}' dotnetai-api 2>/dev/null || true)"
if [[ -n "$PREVIOUS_IMAGE" ]]; then
    ok "currently running image ${PREVIOUS_IMAGE:0:19}"
else
    warn "no running API container — first deploy, so there is no rollback target"
fi

# ---------------------------------------------------------------------------
# PULL FIRST — while the old version is still serving
# ---------------------------------------------------------------------------
# Downloading before stopping anything keeps the outage to the length of a container
# restart rather than the length of a registry pull over the droplet's uplink.
log "Pulling $IMAGE_REF"
docker compose pull --quiet
ok "image pulled"

# ---------------------------------------------------------------------------
# BACK UP THE DATABASE BEFORE MIGRATING IT
# ---------------------------------------------------------------------------
# `docker compose up` runs api-migrate, which alters the schema. A migration that half
# applies, or one that is correct but unwanted, is only recoverable if this ran first.
if docker volume inspect dotnetai_api-data >/dev/null 2>&1; then
    log "Backing up the database"
    mkdir -p "$BACKUP_DIR"
    backup_name="dotnetai-$(date +%Y%m%d-%H%M%S).db"

    # `sqlite3 .backup` would be the correct online backup, but the runtime image has no
    # sqlite3 binary. Stopping the writer for the length of a file copy is the honest
    # alternative: it guarantees a consistent file rather than one caught mid-write. The
    # pull already happened, so this is the only downtime the deploy costs.
    docker compose stop api >/dev/null 2>&1 || true

    if docker run --rm \
        -v dotnetai_api-data:/data \
        -v "$BACKUP_DIR:/backup" \
        alpine:3 sh -c "cp /data/dotnetai.db /backup/$backup_name" 2>/dev/null
    then
        ok "saved $backup_name"
    else
        warn "no database to back up yet (first deploy?)"
    fi

    # Keep the 10 newest. A droplet disk fills up quietly and then everything fails at
    # once.
    #
    # PORTABLE ON PURPOSE. The obvious `find -printf ... | xargs -r` is GNU-only: BSD find
    # (macOS) has no -printf and BSD xargs has no -r, so on a dev machine the pipeline
    # fails, and under `set -e -o pipefail` that aborts the whole deploy - silently, right
    # before the step that would have rolled back. Plain sort works because the names are
    # dotnetai-YYYYMMDD-HHMMSS.db, where lexical order IS chronological order.
    find "$BACKUP_DIR" -maxdepth 1 -type f -name 'dotnetai-*.db' \
        | sort -r | tail -n +11 | while IFS= read -r old_backup; do rm -f "$old_backup"; done
fi

# ---------------------------------------------------------------------------
# START
# ---------------------------------------------------------------------------
log "Starting (migrations run first, then the API)"
# --no-build is the guarantee that this droplet runs exactly what CI produced.
# --wait blocks until api-migrate has exited 0 AND the API's healthcheck passes, so a
# non-zero exit here means the deploy genuinely failed rather than merely started.
if docker compose up -d --no-build --wait --wait-timeout "$HEALTH_TIMEOUT"; then
    ok "containers are up and healthy"
else
    warn "deploy failed — rolling back"
    docker compose logs --tail 40 api-migrate api || true

    if [[ -z "$PREVIOUS_IMAGE" ]]; then
        die "deploy failed and there was no previous image to roll back to"
    fi

    # Re-tag the previous image under the tag compose is configured to run, so `up` brings
    # back exactly the build that was serving before.
    docker tag "$PREVIOUS_IMAGE" "$IMAGE_REF"

    if docker compose up -d --no-build --wait --wait-timeout "$HEALTH_TIMEOUT"; then
        die "rolled back to the previous image. The deploy did NOT go out."
    fi
    die "ROLLBACK ALSO FAILED. The API is down — check 'docker compose logs api'."
fi

# ---------------------------------------------------------------------------
# VERIFY THE PATH A REAL CLIENT TAKES
# ---------------------------------------------------------------------------
# The container healthcheck proves Kestrel is serving, but it runs INSIDE the API container
# and so says nothing about whether Caddy is routing, whether the certificate is valid, or
# whether DNS still points here. When a real domain is configured, check from the outside;
# otherwise fall back to the internal hop.
log "Verifying"
api_domain="$(grep -E '^API_DOMAIN=' .env | cut -d= -f2- | tr -d "\"'" || true)"

if [[ -n "$api_domain" && "$api_domain" != :* ]]; then
    # 401 is the expected answer: the endpoint exists and is refusing an unauthenticated
    # caller. Anything else — 000 (unreachable), 502 (Caddy cannot reach the API), a TLS
    # failure — means the deploy is not actually serving.
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "https://$api_domain/auth/me" || echo 000)"
    if [[ "$code" == "401" ]]; then
        ok "https://$api_domain answers 401 — live, over TLS, enforcing auth"
    else
        warn "https://$api_domain returned $code (expected 401)"
    fi
else
    code="$(docker compose exec -T caddy wget -q -S -O /dev/null http://api:8080/auth/me 2>&1 | awk '/HTTP\//{print $2; exit}' || true)"
    if [[ "$code" == "401" ]]; then
        ok "API answers 401 on the internal network (no domain set, so no external check)"
    else
        warn "unexpected internal status: ${code:-no response}"
    fi
fi

# ---------------------------------------------------------------------------
# TIDY UP
# ---------------------------------------------------------------------------
# Only DANGLING images — never `-a`, which would delete the previous release that a
# rollback still needs.
log "Pruning untagged images"
docker image prune -f >/dev/null
ok "done"

echo
ok "Deployed $IMAGE_REF"
docker compose ps --format 'table {{.Service}}\t{{.Status}}'
