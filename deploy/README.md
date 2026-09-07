# Deploying dotnetai

Two halves, deployed separately from two different repositories:

| Half | Target | Repo |
|---|---|---|
| ASP.NET Core API | Docker Compose on a DigitalOcean droplet | this one |
| React SPA | Firebase Hosting | `my-react-app/` |

```
   ┌──────────────────────────┐          ┌───────────────────────────────┐
   │   Firebase Hosting       │          │   DigitalOcean droplet        │
   │   zane.gabbex.com        │          │   zanet.gabbex.com            │
   │                          │          │                               │
   │   the built SPA          │          │  ┌────────────────────────┐   │
   │   (static files, HTTPS)  │          │  │ caddy   :80 :443       │   │
   └───────────┬──────────────┘          │  │ TLS, the only ports    │   │
               │                         │  └───────────┬────────────┘   │
               │   cross-origin HTTPS    │              │                │
               └────────────────────────►│  ┌───────────┴────────────┐   │
                   fetch + SSE           │  │ api     :8080          │   │
                                         │  │ .NET, non-root         │   │
                                         │  └───────────┬────────────┘   │
                                         │              │                │
                                         │  ┌───────────┴────────────┐   │
                                         │  │ api-data   SQLite      │   │
                                         │  │ api-keys   DP keyring  │   │
                                         │  └────────────────────────┘   │
                                         │                               │
                                         │  api-migrate ── one-shot:     │
                                         │  applies EF migrations, exits │
                                         └───────────────────────────────┘
```

The two sides name each other:

| | is set to | lives in |
|---|---|---|
| `VITE_API_URL` | the **API's** origin | `.env.production` in the SPA repo |
| `WEB_ORIGIN` | the **SPA's** origin | `.env` in this repo |

Constraints this split imposes:

- **The API needs a real certificate.** Firebase Hosting is HTTPS-only and browsers block an
  HTTPS page calling an `http://` endpoint as mixed content — no preflight, nothing in the API
  logs. `API_DOMAIN` must be a real domain, not the droplet's IP.
- **CORS is load-bearing.** Every SPA call is cross-origin and works only if the API lists that
  origin in `Cors:AllowedOrigins`. If the API answers `curl` but not the SPA, suspect CORS first.

---

# Part A — the API on DigitalOcean

## 1. Droplet

| | |
|---|---|
| Image | Ubuntu 24.04 LTS |
| Size | 512 MB / 1 vCPU, **with swap** (`bootstrap.sh` adds 2 GB) |
| Firewall | Open 22, 80, 443 |

On 512 MB, swap is not optional: the API is ~31 MB steady-state and Caddy ~20 MB, but without
swap the first spike gets the API OOM-killed with nothing in its logs. Port 80 is required even
for an HTTPS-only site — that is where Let's Encrypt runs its challenge.

## 2. DNS

`gabbex.com` is on Cloudflare nameservers:

| Type | Name | Content | Proxy status |
|---|---|---|---|
| A | `zanet` | `134.209.108.141` | **DNS only (grey cloud)** |

The proxy must stay grey. With it on, Cloudflare terminates TLS itself, so Caddy's TLS-ALPN
challenge never arrives and ACME retries in a loop while the site appears to work on
Cloudflare's certificate. It also buffers proxied responses and caps connections at 100s on the
free plan, which breaks `/chat` streaming.

```bash
dig +short zanet.gabbex.com     # must print 134.209.108.141 before deploying
```

## 3. Bootstrap (once)

```bash
ssh jeffrey@<droplet-ip>
curl -fsSL https://raw.githubusercontent.com/dyeprey/dotnetai/main/deploy/bootstrap.sh -o /tmp/bootstrap.sh
bash /tmp/bootstrap.sh
```

Download then run — `curl … | bash` makes stdin the pipe, so `sudo` cannot prompt and fails with
"a terminal is required to read the password".

`bootstrap.sh` adds 2 GB swap (`vm.swappiness=10`), installs Docker, adds you to the `docker`
group, caps container log rotation, clones the repo to `/opt/dotnetai` (chowned to you; set
`APP_DIR=~/dotnetai` to change), and creates `.env` from the template.

**Log out and back in afterwards** — group membership only applies to new logins.

## 4. `.env`

```bash
cd /opt/dotnetai && nano .env
openssl rand -base64 48          # JWT_KEY — min 32 chars, or JwtOptions refuses to start
```

```ini
API_DOMAIN=zanet.gabbex.com
ACME_EMAIL=you@example.com

# All THREE SPA origins. Firebase serves every project on both default domains plus the
# custom one; list all three or it breaks on whichever you did not test. No trailing slash.
WEB_ORIGIN=https://dotnetchat-549c6.web.app
WEB_ORIGIN_ALT=https://dotnetchat-549c6.firebaseapp.com
WEB_ORIGIN_CUSTOM=https://zane.gabbex.com

JWT_KEY=<the openssl output>
OPENAI_API_KEY=sk-...
```

```bash
chmod 600 .env
```

## 5. First deploy

Images are built by GitHub Actions on an amd64 runner and pushed to ghcr.io; the droplet only
pulls. (Your Mac is arm64, the droplet amd64, and a 2 GB droplet cannot run `dotnet publish`.)
Each build produces:

```
ghcr.io/dyeprey/dotnetai-api:latest
ghcr.io/dyeprey/dotnetai-api:sha-<full commit sha>
```

ghcr.io packages are private by default. Either make it public (GitHub → Packages →
`dotnetai-api` → Package settings → Change visibility), or log in on the droplet with a
`read:packages` PAT:

```bash
echo '<the-PAT>' | docker login ghcr.io -u dyeprey --password-stdin
```

Then:

```bash
./deploy/deploy.sh
```

Verify:

```bash
curl -s -o /dev/null -w '%{http_code}\n' https://zanet.gabbex.com/auth/me
# 401 — up, reachable over TLS, enforcing auth

curl -si -X OPTIONS https://zanet.gabbex.com/auth/me \
  -H 'Origin: https://zane.gabbex.com' \
  -H 'Access-Control-Request-Method: GET' \
  -H 'Access-Control-Request-Headers: authorization' | grep -i access-control-allow-origin
```

If the second prints nothing, fix `WEB_ORIGIN` before continuing — the SPA will not work.

## 6. Optional: deploy on every push

The workflow's `deploy` job SSHes in and runs `deploy.sh`. It is skipped unless opted in.

```bash
ssh-keygen -t ed25519 -f ~/.ssh/dotnetai_deploy -C 'github-actions deploy' -N ''
ssh-copy-id -i ~/.ssh/dotnetai_deploy.pub jeffrey@<droplet-ip>
ssh-keyscan -H <droplet-ip>            # for DROPLET_KNOWN_HOSTS
```

Settings → Secrets and variables → Actions:

| | name | value |
|---|---|---|
| Secret | `DROPLET_SSH_KEY` | contents of `~/.ssh/dotnetai_deploy` (the **private** key) |
| Secret | `DROPLET_KNOWN_HOSTS` | the `ssh-keyscan` output |
| Secret | `DROPLET_HOST` | the droplet's IP or hostname |
| Secret | `DROPLET_USER` | `jeffrey` |
| Variable | `DEPLOY_ENABLED` | `true` |
| Variable | `DROPLET_APP_DIR` | `/opt/dotnetai` (optional) |

The job pins the host key from `DROPLET_KNOWN_HOSTS` rather than using
`StrictHostKeyChecking=no`, and deploys `sha-<commit>` rather than `latest`.

---

# Part B — the SPA on Firebase Hosting

Run from `my-react-app/` on your own machine.

```bash
cd ../my-react-app
cp .env.production.example .env.production
```

```ini
# .env.production — absolute, https, no trailing slash
VITE_API_URL=https://zanet.gabbex.com
```

```bash
npm ci
npm run build
```

`VITE_API_URL` is inlined at build time, not read at runtime — repointing the SPA means a
rebuild and redeploy. Confirm it landed:

```bash
grep -o 'https://zanet\.gabbex\.com' dist/assets/index-*.js | head -1
```

```bash
npm install -g firebase-tools    # once
firebase login                   # once
firebase use --add               # once — writes .firebaserc
firebase deploy --only hosting
```

`firebase.json` already sets the two things a Vite SPA needs: a catch-all rewrite to
`/index.html` (without it, deep links and refreshes 404), and split caching — `assets/` immutable
for a year (content-hashed), `index.html` `no-store` (its filename never changes).

Check the round trip: open the Firebase URL, register, send a chat message. DevTools → Network
should show a preflight `OPTIONS`, then the real request, with `/chat` arriving as a stream.

---

## Operations

```bash
./deploy/deploy.sh                      # deploy the newest build
docker compose logs -f api              # application logs
docker compose logs api-migrate         # which migrations ran last deploy
docker compose ps                       # health of each service
```

Pushing to `main` runs the 93 tests, builds the image only if they pass, and pushes it.

### Rolling back

```bash
IMAGE_TAG=sha-<the-good-commit-sha> ./deploy/deploy.sh
```

Set `IMAGE_TAG` in `.env` to make it stick; otherwise the next plain run returns to `latest`.
`deploy.sh` also rolls back by itself when a deploy fails its healthcheck, so a failed migration
leaves the last good version serving.

### What deploy.sh does, in order

1. **Preflight** — docker, compose file, and `.env` with `JWT_KEY`, `OPENAI_API_KEY`, `WEB_ORIGIN`
   set. Compose would fail on these too, but only after stopping the running containers.
2. **`git pull --ff-only`** — `docker-compose.yml`, the Caddyfile and this script come from git.
   Skipped if the tree is dirty; a diverged branch stops the deploy.
3. **Records the running image** as the rollback target.
4. **Pulls** before stopping anything, so the outage is a restart, not a download.
5. **Backs up the database**, then applies migrations. Keeps the 10 newest in `backups/`.
6. **`up -d --no-build --wait`** — `--no-build` guarantees the droplet runs what CI tested;
   `--wait` blocks until migrations exit 0 and the API is healthy.
7. **Verifies over TLS from outside** — the container healthcheck runs inside the API container
   and proves nothing about Caddy, the certificate, or DNS.
8. **Rolls back** if any of that failed.

### Ad-hoc database backup

```bash
docker compose stop api
docker run --rm -v dotnetai_api-data:/data -v "$PWD:/backup" alpine:3 \
  sh -c 'cp /data/dotnetai.db /backup/manual-$(date +%F).db'
docker compose start api
```

Changing an allowed origin is API-side only — edit `.env`, then `docker compose up -d api`.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| SPA says "Could not reach the API" but `curl` works | CORS. Run the preflight `curl` from step 5 with the browser's exact origin. Usual causes: trailing slash on `WEB_ORIGIN`, `http` vs `https`, or `.firebaseapp.com` missing while only `.web.app` is listed. |
| Requests blocked as "mixed content" | `VITE_API_URL` is `http://`. Rebuild and redeploy the SPA — the URL is compiled into the bundle. |
| `Failed to determine the https port for redirect.` | Expected and harmless. Caddy terminates TLS; the API only sees plain HTTP. Do **not** set `ASPNETCORE_HTTPS_PORTS` — that causes a redirect loop. |
| HTTPS works but Caddy has no certificate | Cloudflare orange cloud. Check `docker compose logs caddy` for challenge failures; set the record to DNS only. |
| Chat replies arrive all at once, or cut off at ~100s | Cloudflare proxy buffering. Set the record to DNS only. |
| No certificate | Check `dig +short zanet.gabbex.com`, ports 80/443 open, then `docker compose logs caddy`. Let's Encrypt allows 5 duplicate certs per week — never delete the `caddy-data` volume casually. |
| `docker compose pull` → `denied` / `unauthorized` | Private ghcr.io package with no droplet credentials. Make it public or `docker login ghcr.io` with a `read:packages` PAT. |
| Build job fails with `403` pushing to ghcr.io | A repo/org default of "read-only workflow permissions" overrides the workflow's `packages: write`. Settings → Actions → General → Workflow permissions. |
| `exec format error` | An arm64 image on an amd64 droplet — something was pushed by hand from Apple Silicon. Re-run the workflow and redeploy. |
| Deploy job skipped | By design until the repo variable `DEPLOY_ENABLED=true` is set. |
| `api-migrate` exits non-zero | By design the API does not start. Read `docker compose logs api-migrate`, fix, redeploy; the old container keeps serving. |
| Chat not streaming | Something is buffering SSE. The Caddyfile sets `flush_interval -1` and does not compress — look at any proxy or CDN in front of the API. |
| `attempt to write a readonly database` | The `api-data` volume predates the image chowning `/data`. Back up the file, then `docker compose down && docker volume rm dotnetai_api-data && docker compose up -d --build`. |

## Limits

- **SQLite means exactly one API container.** Single file, single writer — `--scale api=3` gives
  lock contention, not throughput. Moving past it means Postgres: swap the EF provider in
  `dotnetai.Api.csproj` and `Program.cs`, then regenerate `Migrations/`.
- **The SPA is not deployed by any of this.** `docker compose up` touches only the API. A change
  to `VITE_API_URL` needs both sides: the API's `WEB_ORIGIN` and an SPA rebuild.
- **No off-droplet backups.** Volumes survive redeploys, not the droplet being destroyed. Enable
  DigitalOcean automated backups, or copy the `.db` file off on a schedule.
