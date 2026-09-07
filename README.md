# dotnetai

**This is an experiment, not a product.** It's me taking .NET 10 for a spin — poking at Minimal
APIs, JWT auth, EF Core and the new `Microsoft.Extensions.AI` abstraction to see how they feel in
a real app rather than in a tutorial snippet.

It works, it's deployed, and it has tests. But it exists to answer "how does .NET do this?", so
expect it to change shape whenever something looks worth trying differently. Don't depend on it.

## What it is

A small chat app: register, log in, talk to an LLM. The API is ASP.NET Core; the React SPA lives
in a separate repo (`my-react-app/`).

**Live at [zane.gabbex.com](https://zane.gabbex.com)** — front-end on Firebase Hosting, API on
`zanet.gabbex.com`.

The parts that were actually interesting to build:

- **Auth** — JWT access tokens plus refresh-token rotation, with reuse detection: presenting an
  already-spent refresh token revokes every session for that user.
- **Streaming** — `/chat` is Server-Sent Events via `TypedResults.ServerSentEvents`, so replies
  arrive token by token.
- **Provider abstraction** — the app depends on `IChatClient`, not on the OpenAI SDK. Swapping
  providers is one registration in `Program.cs`; the tests substitute a fake and never hit the
  network.
- **Rate limiting** — partitioned per user on the `sub` claim, because `/chat` costs money.

## Stack

.NET 10 · ASP.NET Core Minimal APIs · EF Core + SQLite · `Microsoft.Extensions.AI` · React + Vite
· Docker Compose + Caddy on a DigitalOcean droplet · Firebase Hosting for the SPA

## Running it

Needs the .NET 10 SDK and an OpenAI API key.

```bash
cd dotnetai.Api
dotnet user-secrets set "Jwt:Key" "$(openssl rand -base64 48)"
dotnet user-secrets set "OpenAI:ApiKey" "sk-..."
dotnet run
```

The API comes up on `http://localhost:5231`. Both secrets are required — the app refuses to boot
without them rather than failing on the first request. `appsettings.Development.json` already
allows the Vite dev server's origin (`localhost:5173`).

```bash
dotnet test        # 93 integration tests, from the solution root
```

## More

- [`dotnetai.Api.Tests/README.md`](dotnetai.Api.Tests/README.md) — what the tests cover and how
  they boot the app in memory
- [`deploy/README.md`](deploy/README.md) — the droplet, TLS, CI, and the SPA on Firebase

## Caveats

SQLite means exactly one API container. Access tokens can't be revoked before they expire. There
are no off-droplet backups. All acceptable for an experiment — all things that would need fixing
if this were ever meant to be more than one.
