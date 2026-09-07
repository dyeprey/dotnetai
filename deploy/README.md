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
| Size | **2 GB RAM minimum** |
| Options | Enable the DigitalOcean VPC firewall, then open ports 22, 80, 443 |

> **Why 2 GB.** The 1 GB droplet is enough to *run* the API but not to *build* it —
> `dotnet publish` peaks well above 512 MB and the OOM killer takes the build down with an
> error that blames neither. If you are set on the 1 GB size, add swap first:
>
> ```bash
> fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile
> echo '/swapfile none swap sw 0 0' >> /etc/fstab
> ```

## 2. Point DNS at it

An **A record** for the API's subdomain → the droplet's IPv4 address. Let it propagate
before step 5: Caddy proves domain ownership over ports 80 and 443 the first time it starts,
and if the record is not live yet, issuance fails and you wait out a retry backoff.

```bash
dig +short api.example.com    # must print the droplet's IP before you continue
```

## 3. Install Docker on the droplet

```bash
ssh root@<droplet-ip>
curl -fsSL https://get.docker.com | sh
```

## 4. Get the code and configure it

```bash
git clone git@github.com:dyeprey/dotnetai.git /opt/dotnetai
cd /opt/dotnetai
cp .env.example .env
```

Everything the droplet needs — `docker-compose.yml`, `Dockerfile`, `deploy/Caddyfile` — is
in this repository, so the clone is the whole deployment.

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

## 5. Build and start

```bash
docker compose up -d --build --wait
```

> **Build on the droplet, not on your Mac.** Apple Silicon builds `arm64` images;
> DigitalOcean's standard droplets are `amd64`, and an image of the wrong architecture dies
> at `exec format error`. Building on the target machine sidesteps the question. To build
> locally and push to a registry instead, cross-build explicitly:
>
> ```bash
> docker buildx build --platform linux/amd64 -t <registry>/dotnetai-api:latest . --push
> ```

`--wait` holds until every healthcheck passes, so the command failing means the deploy
failed — worth having in any script that runs this.

Confirm:

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
moving on — it is much easier to diagnose here than from the browser.

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
docker compose logs -f api             # application logs
docker compose logs api-migrate        # exactly which migrations ran on the last deploy
docker compose ps                      # health of each service

git pull && docker compose up -d --build --wait     # deploy a new version

# Back up the database — do this before any deploy that migrates
docker compose run --rm -v "$PWD:/backup" --entrypoint sh api \
  -c 'cp /data/dotnetai.db /backup/dotnetai-$(date +%F).db'

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
