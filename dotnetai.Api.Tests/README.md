# dotnetai.Api.Tests

Integration tests for the Minimal API endpoints. `dotnet test` from the solution root.

## Why integration tests rather than unit tests

Almost nothing worth asserting about these endpoints lives in a handler method. That
`UseAuthentication` runs before `UseAuthorization`; that a missing token is 401 while a wrong
role is 403; that `MapInboundClaims = false` leaves `sub` spelled `sub`; that the unique index
actually stops a duplicate registration; that a 401 still carries CORS headers — every one of
those is a property of the *pipeline*. Call the handlers directly and none of them are covered.

`ApiFactory` boots the real app in memory via `WebApplicationFactory<Program>`: real middleware,
real DI, real JWT validation, no socket. Each test class gets its own SQLite file in the temp
directory, deleted on dispose, so classes running in parallel cannot see each other's data and
the dev database is never touched.

## What is covered

| File | Focus |
| --- | --- |
| `AuthRegisterTests` | Validation, password hashing, email normalization, mass assignment, account enumeration |
| `AuthLoginTests` | Credential checking, case handling, hashed refresh-token storage, independent sessions |
| `AuthMeTests` | Claim plumbing, and every clause of `TokenValidationParameters` — forged, tampered, expired, wrong issuer, wrong audience |
| `AuthRefreshTests` | Rotation, the T1→T2→T3 chain, reuse detection and its blast radius, role changes |
| `AuthLogoutTests` | Revocation, idempotency, and the honest limit of stateless JWTs |
| `DemoEndpointsTests` | The three access levels, 401-vs-403, no hash leakage |
| `CorsTests` | Policy headers, middleware ordering, unlisted origins |
| `ChatEndpointsTests` | Streaming, up-front validation, prompt construction, history trimming, per-user rate limiting |

`TestTokens` mints deliberately *wrong* JWTs. The app's own `TokenService` only ever produces
correct ones — which is right for production code and useless for proving a bad token is
rejected.

`EchoChatClient` and `ThrowingChatClient` stand in for OpenAI. This is the payoff of depending on
`IChatClient` rather than on the OpenAI SDK: the alternatives are calling the real API (slow,
non-deterministic, billed, and impossible on CI without a key) or faking OpenAI's wire format
with an HTTP handler, which tests the shape of somebody else's JSON instead of anything of ours.
`EchoChatClient.LastPrompt` records what the endpoint actually sent to the model, which is how
the system prompt and the history trimming get asserted at all — neither is visible in the
response.

## The one thing that is different about `/chat`

Its status code is decided *before* its body exists. `TypedResults.ServerSentEvents` commits the
200 and the headers immediately, so nothing afterwards can turn the response into a 400 or a
500. Two consequences the tests pin:

- every rejection (empty conversation, blank message, oversized message) happens **up front**,
  in the part of the handler that can still return a status code;
- a failure *after* streaming starts arrives as an SSE `error` event on a 200, because there is
  no status code left to change.

## These tests were mutation-checked

A suite that never fails proves nothing. Each of these was introduced deliberately and the suite
confirmed to go red, then reverted:

- middleware order reversed (`UseAuthorization` before `UseAuthentication`)
- `app.UseCors` moved after the auth middleware
- `MapInboundClaims = true`; `RoleClaimType` removed
- `ValidateLifetime` / `ValidateAudience` disabled
- email normalization dropped from registration
- `409 Conflict` on duplicate registration; `404` for an unknown login
- reuse detection removed; rotation removed
- `/admin/users` returning entities instead of `UserSummary`
- `RequireAuthorization` dropped; `AdminOnly` downgraded
- the system prompt not prepended to the conversation
- history trimmed from the front of the transcript instead of the back
- an unrecognised message role defaulting to `Assistant` rather than `User`
- empty deltas forwarded as SSE frames instead of skipped
- `RequireRateLimiting` dropped from `/chat`
- the empty-conversation check removed
- the raw exception message returned to the browser instead of a generic one

One mutation did **not** go red, and it was the code that was wrong rather than the test:
setting `ValidateIssuerSigningKey = false` changes nothing about signature verification. That
flag gates validation of the *key object* (for an X.509 key, the certificate's validity period);
signatures are always verified against `IssuerSigningKey`. The comment in `Program.cs` has been
corrected to say so.
