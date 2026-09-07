#!/usr/bin/env bash
#
# ONE-TIME droplet setup. Run once, on a fresh droplet, as a user with sudo:
#
#   ssh jeffrey@<droplet-ip>
#   curl -fsSL https://raw.githubusercontent.com/dyeprey/dotnetai/main/deploy/bootstrap.sh | bash
#
# ...or clone first and run ./deploy/bootstrap.sh. Afterwards you only ever run deploy.sh.
#
# Installs Docker, adds swap, clones the repo to /opt/dotnetai, and leaves you one step
# from deploying: filling in .env.

set -Eeuo pipefail

REPO_URL="${REPO_URL:-https://github.com/dyeprey/dotnetai.git}"
APP_DIR="${APP_DIR:-/opt/dotnetai}"
SWAP_SIZE="${SWAP_SIZE:-2G}"

log()  { printf '\033[1;34m▸\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m✓\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

[[ $EUID -ne 0 ]] || die "run this as your normal user, not root — it uses sudo where needed"
sudo -v || die "this needs sudo"

# ---------------------------------------------------------------------------
# SWAP — the single most important step on a 512 MB droplet
# ---------------------------------------------------------------------------
# Without it, the first memory spike does not slow the box down, it kills a process. The
# OOM killer picks the largest RSS, which is the API — so the symptom is "the API randomly
# disappears" with nothing useful in its own logs, because it never got to write any.
if swapon --show | grep -q .; then
    ok "swap already configured ($(swapon --show=SIZE --noheadings | tr -d ' ' | tr '\n' ' '))"
else
    log "Adding ${SWAP_SIZE} of swap"
    sudo fallocate -l "$SWAP_SIZE" /swapfile
    sudo chmod 600 /swapfile
    sudo mkswap /swapfile >/dev/null
    sudo swapon /swapfile
    grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab >/dev/null

    # Default swappiness of 60 swaps eagerly even with RAM free, which is slow. 10 keeps
    # swap as the safety net it is here rather than a routine tier of memory.
    echo 'vm.swappiness=10' | sudo tee /etc/sysctl.d/99-swappiness.conf >/dev/null
    sudo sysctl -q vm.swappiness=10
    ok "swap active"
fi

# ---------------------------------------------------------------------------
# DOCKER
# ---------------------------------------------------------------------------
if command -v docker >/dev/null && docker compose version >/dev/null 2>&1; then
    ok "docker already installed ($(docker --version))"
else
    log "Installing Docker"
    curl -fsSL https://get.docker.com | sudo sh
    ok "docker installed"
fi

# Running docker without sudo. The group change only applies to NEW logins, which is why
# the message at the end tells you to reconnect — deploy.sh calls docker directly.
if ! groups | tr ' ' '\n' | grep -qx docker; then
    log "Adding $USER to the docker group"
    sudo usermod -aG docker "$USER"
    NEEDS_RELOGIN=1
    ok "added (takes effect on your next login)"
else
    NEEDS_RELOGIN=0
    ok "$USER is already in the docker group"
fi

# Cap the journal so logs cannot quietly eat a small disk.
sudo mkdir -p /etc/docker
if [[ ! -f /etc/docker/daemon.json ]]; then
    printf '%s\n' '{"log-driver":"json-file","log-opts":{"max-size":"10m","max-file":"3"}}' \
        | sudo tee /etc/docker/daemon.json >/dev/null
    sudo systemctl restart docker 2>/dev/null || true
    ok "container log rotation capped at 3x10MB"
fi

# ---------------------------------------------------------------------------
# THE REPO
# ---------------------------------------------------------------------------
if [[ -d "$APP_DIR/.git" ]]; then
    ok "$APP_DIR already cloned"
else
    log "Cloning into $APP_DIR"
    sudo mkdir -p "$APP_DIR"
    sudo chown "$USER:$USER" "$APP_DIR"
    # HTTPS rather than SSH: the droplet has no deploy key, and it only ever needs to read.
    git clone "$REPO_URL" "$APP_DIR"
    ok "cloned"
fi

cd "$APP_DIR"
if [[ ! -f .env ]]; then
    cp .env.example .env
    chmod 600 .env
    ok "created .env from the template (chmod 600)"
else
    ok ".env already exists — left untouched"
fi

# ---------------------------------------------------------------------------
# FIREWALL
# ---------------------------------------------------------------------------
# Caddy needs 80 and 443; 80 is not optional even for an HTTPS-only site, because that is
# where Let's Encrypt performs the challenge.
if command -v ufw >/dev/null && sudo ufw status | grep -q 'Status: active'; then
    for port in 80 443; do
        sudo ufw allow "$port"/tcp >/dev/null 2>&1 || true
    done
    ok "ufw allows 80 and 443"
else
    warn "ufw is not active — make sure the DigitalOcean firewall opens 22, 80 and 443"
fi

echo
ok "Bootstrap complete"
cat <<EOF

Next:

  1. Edit the secrets and origins:
       nano $APP_DIR/.env

     Required:
       API_DOMAIN        api.yourdomain.com   (or ':80' to test on the bare IP first)
       JWT_KEY           $(openssl rand -base64 48 2>/dev/null || echo '<openssl rand -base64 48>')
       OPENAI_API_KEY    sk-...
       WEB_ORIGIN        https://your-project.web.app

  2. Deploy:
       cd $APP_DIR && ./deploy/deploy.sh

EOF
[[ "$NEEDS_RELOGIN" == "1" ]] && warn "Log out and back in first, so the docker group applies."
exit 0
