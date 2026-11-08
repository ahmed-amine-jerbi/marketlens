# 0006 — Cookie sign-in for web, bearer tokens for mobile

- **Status:** accepted, 2026-11-08

## Context

Accounts are optional and used for synced watchlists, settings and price alerts. Sign-in is email
and password (with email confirmation) or Google. The web app is served from the same origin as the
API (Vite proxy locally, nginx in Docker); the mobile app is a native client.

## Decision

ASP.NET Core Identity with both of its schemes:
- **Web:** an HttpOnly, SameSite=Lax application cookie, `Secure` whenever served over HTTPS.
  State-changing requests must be JSON and carry `X-Requested-With: marketlens`, which a cross-site
  form can't send. No credentialed CORS.
- **Mobile:** Identity's bearer and refresh tokens, stored in `expo-secure-store`.
- **Google:** the client obtains a Google ID token; the API verifies it (signature, audience,
  issuer, expiry, `email_verified`) and signs the user in with the scheme that client uses. An
  unconfirmed local account with the same email loses its password before linking; a confirmed one
  links only after the user signs in with its password. This blocks account pre-hijacking.
- Data Protection keys are stored in PostgreSQL so sessions survive container rebuilds.

## Consequences

- No token ever sits in browser JavaScript, so a script injection can't read it.
- Two schemes to test; endpoints accept either.

## Alternatives rejected

- **Bearer tokens in the browser:** tokens readable by JavaScript and stored in web storage.
- **A separate identity server:** more to run than an optional account needs.
