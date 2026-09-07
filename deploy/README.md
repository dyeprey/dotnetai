# Deploying dotnetai

Two halves, deployed separately:

- **The ASP.NET Core API** → Docker Compose on a DigitalOcean droplet (**this repository**)
- **The React SPA** → Firebase Hosting (a **separate** repository/directory, `my-react-app/`)

> The two live in different git repositories. Everything the droplet needs is in this one;
> Part B below is run from wherever you keep the SPA.

```
   ┌──────────────────────────┐          ┌───────────────────────────────┐
   │   Firebase Hosting       │          │   DigitalOcean droplet        │
   │   your-project.web.app   │          │   api.example.com             │
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

## The two things this split makes non-negotiable

**1. The API needs a real certificate.** Firebase Hosting is HTTPS-only, and a browser
refuses to let an HTTPS page call an `http://` endpoint. The request is blocked as *mixed
content* before it is ever sent — no preflight, nothing in the API's logs, and a console
error that does not mention TLS. So `API_DOMAIN` must be a real domain, not the droplet's
bare IP. Caddy obtains and renews the certificate for it automatically.

**2. CORS is now load-bearing.** The SPA's origin and the API's origin genuinely differ, so
every call the SPA makes is cross-origin and succeeds only because the API names that origin
in `Cors:AllowedOrigins`. This is the setting most likely to be wrong on first deploy, and
its failure mode is deliberately unhelpful: the browser refuses to tell JavaScript whether a
failed request was a CORS rejection or an unreachable server, so the SPA reports "Could not
reach the API" either way. **If the API answers `curl` but not the SPA, suspect CORS first.**

The two sides name each other, which is the easiest thing in this deploy to get half-right:

| | is set to | lives in |
|---|---|---|
| `VITE_API_URL` | the **API's** origin | `.env.production` in the SPA repo |
| `WEB_ORIGIN` | the **SPA's** origin | `.env` in this repo |

---

# Part A — the API on DigitalOcean

## 1. Create the droplet

| | |
|---|---|
| Image | Ubuntu 24.04 LTS |
| Size | 512 MB / 1 vCPU works — **add swap** (bootstrap.sh does) |
| Options | Open ports 22, 80 and 443 in the DigitalOcean firewall |

> **On 512 MB, swap is not optional.** The API measures ~31 MB steady-state and Caddy ~20 MB,
> so it fits — but without swap the first memory spike does not slow the box down, it kills
> a process. The OOM killer picks the largest RSS, which is the API, so the symptom is "the
> API randomly disappears" with nothing in its own logs, because it never got to write any.
>
> Port 80 is required even though the site is HTTPS-only: that is where Let's Encrypt
> performs its challenge.

## 2. Point DNS at it

An **A record** for the API's subdomain → the droplet's IPv4 address. Let it propagate
before you deploy: Caddy proves domain ownership over ports 80 and 443 the first time it
starts, and if the record is not live yet, issuance fails and you wait out a retry backoff.

```bash
dig +short api.example.com    # must print the droplet's IP before you continue
```

You can skip this for a first smoke test by setting `API_DOMAIN=:80` and hitting the bare
IP — but the Firebase-hosted SPA cannot talk to an HTTP API (mixed content), so a real
deployment needs the domain.

## 3. Bootstrap the droplet (once)

```bash
ssh jeffrey@<droplet-ip>
curl -fsSL https://raw.githubusercontent.com/dyeprey/dotnetai/main/deploy/bootstrap.sh | bash
```

That adds 2 GB of swap (with `vm.swappiness=10`, so swap stays a safety net rather than a
routine tier of memory), installs Docker, puts you in the `docker` group, caps container
log rotation so logs cannot fill a small disk, clones the repo to `/opt/dotnetai`, and
creates `.env` from the template.

**Log out and back in afterwards** — group membership only applies to new logins, and
`deploy.sh` calls `docker` directly.

## 4. Fill in .env

```bash
cd /opt/dotnetai && nano .env
```

Everything the droplet needs — `docker-compose.yml`, `deploy/Caddyfile`, `deploy/deploy.sh`
— is in this repository, so the clone bootstrap.sh made is the whole deployment.

Generate a signing key — at least 32 characters, because that is the minimum for
HMAC-SHA256 and `JwtOptions` refuses to start on anything shorter:

```bash
openssl rand -base64 48
```

Then edit `.env`:

```ini
API_DOMAIN=api.example.com
ACME_EMAIL=you@example.com

# Firebase serves every project on BOTH of these and the SPA works on either, so list both
# or the site breaks on whichever one you did not test. No trailing slash.
WEB_ORIGIN=https://your-project.web.app
WEB_ORIGIN_ALT=https://your-project.firebaseapp.com
WEB_ORIGIN_CUSTOM=            # a custom domain on Firebase Hosting, if you have one

JWT_KEY=<the openssl output>
OPENAI_API_KEY=sk-...
```

```bash
chmod 600 .env      # it holds the signing key and the OpenAI key
```

## 5. First deploy

The image is **built by GitHub Actions on an amd64 runner and pushed to ghcr.io**. The
droplet only ever pulls. That is what removes the two constraints you would otherwise be
fighting: your Mac is arm64 and the droplet is amd64, and a 2 GB droplet cannot run
`dotnet publish` without swapping itself to death.

Push the workflow once and let the first build finish (Actions tab → *build and deploy*).
It produces two tags:

```
ghcr.io/dyeprey/dotnetai-api:latest
ghcr.io/dyeprey/dotnetai-api:sha-<full commit sha>
```

### Let the droplet pull the image

Packages on ghcr.io are **private by default**, so pick one:

**Public package** (simplest — the image is not secret, your `.env` is):
GitHub → your profile → Packages → `dotnetai-api` → Package settings → Change visibility →
Public. The droplet then needs no registry credentials at all.

**Private package**: create a PAT with only the `read:packages` scope, then on the droplet:

```bash
echo '<the-PAT>' | docker login ghcr.io -u dyeprey --password-stdin
```

### Deploy

```bash
./deploy/deploy.sh
```

That is the whole deploy, and it is the same command forever after. It pulls the image,
backs up the database, applies migrations, starts the API, verifies it over TLS, and
**rolls back to the previous image if anything fails**.

```bash
curl -s -o /dev/null -w '%{http_code}\n' https://api.example.com/auth/me
# 401 — the API is up, reachable over TLS, and enforcing auth

# CORS: the SPA's origin must come back echoed in Access-Control-Allow-Origin.
curl -si -X OPTIONS https://api.example.com/auth/me \
  -H 'Origin: https://your-project.web.app' \
  -H 'Access-Control-Request-Method: GET' \
  -H 'Access-Control-Request-Headers: authorization' | grep -i access-control-allow-origin
```

If that second command prints nothing, the SPA will not work. Fix `WEB_ORIGIN` before
moving on — much easier to diagnose here than from the browser.

## 6. Optional: deploy automatically on every push

The workflow's `deploy` job SSHes into the droplet and runs `deploy.sh` for you. It is
**skipped unless you opt in**, so the workflow stays green before any of this exists.

Generate a key used for nothing else, so revoking it costs you nothing:

```bash
ssh-keygen -t ed25519 -f ~/.ssh/dotnetai_deploy -C 'github-actions deploy' -N ''
ssh-copy-id -i ~/.ssh/dotnetai_deploy.pub jeffrey@<droplet-ip>
ssh-keyscan -H <droplet-ip>            # for DROPLET_KNOWN_HOSTS
```

Then in the repo — Settings → Secrets and variables → Actions:

| | name | value |
|---|---|---|
| Secret | `DROPLET_SSH_KEY` | contents of `~/.ssh/dotnetai_deploy` (the **private** key) |
| Secret | `DROPLET_KNOWN_HOSTS` | the `ssh-keyscan` output |
| Secret | `DROPLET_HOST` | the droplet's IP or hostname |
| Secret | `DROPLET_USER` | `jeffrey` |
| Variable | `DEPLOY_ENABLED` | `true` |
| Variable | `DROPLET_APP_DIR` | `/opt/dotnetai` (optional) |

The job pins the host key from `DROPLET_KNOWN_HOSTS` rather than using
`StrictHostKeyChecking=no`. That matters: disabling the check would make the job accept any
machine answering on that address — which is exactly the machine you are handing a deploy
key to.

It deploys `sha-<commit>`, not `latest`, so what runs on the droplet is pinned to the
commit that triggered it rather than to whatever `latest` meant by the time SSH connected.

---

# Part B — the SPA on Firebase Hosting

Run these from the SPA's own directory (`my-react-app/`), on your own machine — it is a
separate repository from this one.

## 1. Configure and build

```bash
cd ../my-react-app          # wherever you keep the SPA
cp .env.production.example .env.production
```

```ini
# .env.production — absolute, https, no trailing slash
VITE_API_URL=https://api.example.com
```

```bash
npm ci
npm run build
```

> **`VITE_API_URL` is inlined at build time, not read at runtime.** Vite substitutes it into
> the JavaScript as a literal string, so pointing the SPA at a different API means
> rebuilding and redeploying. There is no environment variable to change on Firebase
> afterwards. Confirm it landed:
>
> ```bash
> grep -o 'https://api\.example\.com' dist/assets/index-*.js | head -1
> ```

## 2. Deploy

```bash
npm install -g firebase-tools    # once
firebase login                   # once
firebase use --add               # once — pick the project, this writes .firebaserc
firebase deploy --only hosting
```

`firebase.json` is already configured with the two things a Vite SPA needs:

- **A catch-all rewrite to `/index.html`.** react-router owns the URL space; Firebase sees
  a file that does not exist. Without this, a deep link or a refresh anywhere but `/`
  returns Firebase's 404 — the most common way a correct SPA build looks broken once
  deployed.
- **Split caching.** Everything in `assets/` is content-hashed by Vite, so it is cached
  immutably for a year; `index.html` is `no-store`, because it is the one filename that
  never changes. Cache it and a deploy strands returning visitors on an old index pointing
  at asset filenames that no longer exist — a blank page that a hard refresh "fixes".

## 3. Check the round trip

Open the Firebase URL, register an account, and send a chat message. In DevTools → Network
you should see a preflight `OPTIONS` followed by the real request, and `/chat` arriving as a
stream rather than in one lump.

---

## Day-to-day operations

```bash
# --- API (on the droplet) ---
./deploy/deploy.sh                      # deploy the newest build
docker compose logs -f api              # application logs
docker compose logs api-migrate         # exactly which migrations ran last deploy
docker compose ps                       # health of each service
```

Pushing to `main` is the normal path: CI runs the 93 tests, builds the image only if they
pass, and pushes it. Then either the `deploy` job runs `deploy.sh` for you, or you SSH in
and run it yourself.

### Rolling back

Every build is tagged with its commit, so a rollback is a tag and a re-run — no rebuild
anywhere, and no waiting on CI:

```bash
IMAGE_TAG=sha-<the-good-commit-sha> ./deploy/deploy.sh
```

To make it stick across future deploys, set `IMAGE_TAG` in `.env` instead of passing it
inline; otherwise the next plain `./deploy/deploy.sh` returns you to `latest`.

`deploy.sh` also rolls back **by itself** when a deploy fails its healthcheck — it re-tags
the image that was running and brings it back, then exits non-zero. A failed migration
therefore leaves you on the last good version rather than down.

### What deploy.sh does, in order

1. **Preflight** — docker, compose file, and `.env` with `JWT_KEY`, `OPENAI_API_KEY` and
   `WEB_ORIGIN` actually set. Compose would fail on these anyway, but only *after* stopping
   the running containers; checking first turns an outage into a no-op.
2. **`git pull --ff-only`** — the image comes from the registry, but `docker-compose.yml`,
   the Caddyfile and this script come from git. Skipped if the tree is dirty; `--ff-only`
   so a diverged branch stops the deploy instead of silently merging.
3. **Records the running image**, as the rollback target.
4. **Pulls** — before stopping anything, so the outage is a container restart rather than a
   registry download.
5. **Backs up the database**, then applies migrations. Keeps the 10 newest in `backups/`.
6. **`up -d --no-build --wait`** — `--no-build` is the guarantee the droplet runs exactly
   what CI tested; `--wait` blocks until migrations exited 0 *and* the API is healthy.
7. **Verifies over TLS** from outside, because the container healthcheck runs *inside* the
   API container and proves nothing about Caddy routing, the certificate, or DNS.
8. **Rolls back** if any of that failed.

```bash
# Ad-hoc database backup (deploy.sh does this automatically before each migration)
docker compose stop api
docker run --rm -v dotnetai_api-data:/data -v "$PWD:/backup" alpine:3 \
  sh -c 'cp /data/dotnetai.db /backup/manual-$(date +%F).db'
docker compose start api

# --- SPA (from the separate my-react-app/ directory) ---
npm run build && firebase deploy --only hosting
```

Changing an allowed origin is API-side only and needs no rebuild:

```bash
# edit .env, then
docker compose up -d api
```

## Troubleshooting

**The SPA says "Could not reach the API" but `curl` works.** Almost always CORS. The browser
deliberately refuses to distinguish a CORS rejection from an unreachable server, so the SPA
cannot tell you which it was. Run the preflight `curl` from Part A step 5 with the exact
origin the browser is using, and check for the three usual causes: a trailing slash on
`WEB_ORIGIN`, `http` where the browser sends `https`, or the `.firebaseapp.com` domain
missing while only `.web.app` is listed.

**Requests blocked as "mixed content".** `VITE_API_URL` is `http://`. It has to be `https://`
— see the note at the top. Rebuild and redeploy the SPA; a Firebase config change will not
fix it, because the URL is compiled into the bundle.

**`Failed to determine the https port for redirect.`** Expected, and harmless. `Program.cs`
calls `UseHttpsRedirection()` outside Development, but the API only ever receives plain HTTP
from Caddy on the internal network, and no HTTPS port is configured for it to redirect to.
The middleware does nothing and logs this once. TLS and the HTTP→HTTPS redirect both happen
at the edge, in Caddy, which is where they belong. Do **not** "fix" it by setting
`ASPNETCORE_HTTPS_PORTS` — that makes the API redirect requests Caddy has already decrypted,
and the browser sees a redirect loop.

**No certificate.** Check the A record resolves to this droplet (`dig +short api.example.com`),
that ports 80 and 443 are open in the DigitalOcean firewall, then `docker compose logs caddy`.
Let's Encrypt allows **5 duplicate certificates per week**; the `caddy-data` volume exists so
redeploys reuse the certificate you already have, so never delete that volume casually.

**`docker compose pull` fails with `denied` or `unauthorized`.** The ghcr.io package is
private and the droplet has no credentials. Either make the package public (Packages →
Package settings → Change visibility) or `docker login ghcr.io` with a `read:packages` PAT.

**The build job fails with `403` pushing to ghcr.io.** The workflow needs
`permissions: packages: write`. It is set — but a repository- or org-level default of
"read-only workflow permissions" overrides it. Settings → Actions → General → Workflow
permissions.

**`exec format error` when a container starts.** An arm64 image on an amd64 droplet. The
workflow pins `platforms: linux/amd64`, so this means something was built and pushed by
hand from an Apple Silicon machine. Re-run the workflow and redeploy.

**The deploy job is skipped.** By design, until you set the repository variable
`DEPLOY_ENABLED=true`. Build-and-push still runs; only the SSH step is gated.

**`api-migrate` exits non-zero.** The API is not started — that is by design. Read
`docker compose logs api-migrate`, fix the migration, redeploy. The old container keeps
serving in the meantime.

**Chat arrives all at once instead of streaming.** Something is buffering the SSE response.
The Caddyfile sets `flush_interval -1` and deliberately does not compress; if you have put
another proxy or a CDN in front of the API, that is where to look.

**`attempt to write a readonly database`.** The `api-data` volume was created before the
image chowned `/data`. Back up the file, then `docker compose down && docker volume rm
dotnetai_api-data && docker compose up -d --build`.

## Two limits worth knowing before you grow into them

**SQLite means exactly one API container.** SQLite is a single file with a single writer, so
`docker compose up --scale api=3` produces lock contention and corruption, not throughput.
That is a fine trade for one droplet — but the ceiling is real, and moving past it means
Postgres: swap the EF provider in `dotnetai.Api.csproj` and `Program.cs`, then regenerate
`Migrations/`.

**The SPA is not deployed by any of this.** `docker compose up` on the droplet touches only
the API. A change to the React app needs `npm run build && firebase deploy` from the SPA
repo, and a change to `VITE_API_URL` needs both — the API's `WEB_ORIGIN` and the SPA's
rebuild.

**No off-droplet backups.** The volumes survive redeploys and container restarts. They do not
survive the droplet being destroyed. Enable DigitalOcean's automated backups, or copy the
`.db` file somewhere else on a schedule.
