# dotnetai.Api.Tests

Integration tests for the Minimal API endpoints.

```bash
dotnet test          # from the solution root
```

## How the tests run

`ApiFactory` boots the app in memory via `WebApplicationFactory<Program>` — real middleware, real
DI, real JWT validation, no socket. Each test class gets its own SQLite file in the temp
directory, deleted on dispose, so parallel classes cannot see each other's data and the dev
database is never touched.

`TestTokens` mints deliberately invalid JWTs (forged, tampered, expired, wrong issuer, wrong
audience); the app's own `TokenService` only produces valid ones.

`EchoChatClient` and `ThrowingChatClient` implement `IChatClient` in place of OpenAI, so no test
needs a key or a network. `EchoChatClient.LastPrompt` records what the endpoint sent to the
model, which is how the system prompt and history trimming are asserted.

## Coverage

| File | Focus |
| --- | --- |
| `AuthRegisterTests` | Validation, password hashing, email normalization, mass assignment, account enumeration |
| `AuthLoginTests` | Credential checking, case handling, hashed refresh-token storage, independent sessions |
| `AuthMeTests` | Claim plumbing and every clause of `TokenValidationParameters` |
| `AuthRefreshTests` | Rotation, the T1→T2→T3 chain, reuse detection and its blast radius, role changes |
| `AuthLogoutTests` | Revocation and idempotency |
| `DemoEndpointsTests` | The three access levels, 401-vs-403, no hash leakage |
| `CorsTests` | Policy headers, middleware ordering, unlisted origins |
| `ChatEndpointsTests` | Streaming, up-front validation, prompt construction, history trimming, per-user rate limiting |

## `/chat` streaming

`TypedResults.ServerSentEvents` commits the 200 and headers before the body exists, so:

- every rejection (empty conversation, blank message, oversized message) is asserted **up front**,
  while the handler can still return a status code;
- a failure after streaming starts arrives as an SSE `error` event on a 200.

Note: `ValidateIssuerSigningKey = false` does not weaken signature verification — that flag gates
validation of the key object (for an X.509 key, the certificate's validity period). Signatures are
always verified against `IssuerSigningKey`.
