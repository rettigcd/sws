# Feature: Automatic B2C / OpenID Connect / OAuth Automation Engine

## Goal

Provide an authentication engine capable of reproducing previously detected authentication flows using the output of the Authentication Detector.

The engine should automatically execute authentication exchanges on behalf of the caller and return authenticated session state (tokens, cookies, claims, expiration data, etc.).

The engine must support replaying flows against the same:

- Authorization Server
- Client Application
- User Identity
- Policy/Tenant (for B2C systems)

without requiring the caller to understand protocol details.

---

## Design Principles

- Protocol-aware rather than pure HTTP replay.
- Prefer dynamically discovered values over caller-supplied values.
- Support deterministic testing through mocked dependencies.
- Allow partial overrides without requiring complete customization.
- Never store secrets in logs or serialized diagnostics.

## Scope Decisions

The automater is a protocol engine, not a browser emulator.

It should understand and execute OAuth / OpenID Connect / Azure AD B2C flows using protocol semantics. Captured HTTP sessions are used to infer flow shape, required variables, endpoints, cookies, form posts, and transition rules, but the engine should not blindly replay every request byte-for-byte.

The first supported provider family is Azure AD B2C / Microsoft Entra External ID. The design must allow future providers such as Okta, Auth0, Keycloak, and custom OpenID Connect providers through pluggable flow handlers.

## Explicitly Out of Scope

The first version does not support:

- MFA
- WebAuthn / FIDO2
- JavaScript execution
- CAPTCHA
- Browser automation
- Persisting and resuming serialized authentication sessions
- Running multiple authentication sessions concurrently on the same engine instance

If any of these are detected, the engine should fail with a structured `UnsupportedFlowReason`.

## Supported Flow Behavior

The engine should:

- Accept the Detector output as its primary input.
- Execute the detected authentication flow using protocol-aware steps.
- Use a mockable HTTP client abstraction.
- Maintain an in-memory execution state machine.
- Track generated, discovered, extracted, and caller-supplied variables.
- Prefer values discovered during execution over values copied from the original captured session.
- Allow caller overrides.
- Automatically refresh access tokens when a refresh token is available.
- Submit login forms only when the original detected session contained a form-based login step.
- Parse hidden HTML inputs and anti-forgery fields when needed.
- Follow HTTP redirects according to the detected flow rules.
- Capture authorization codes from redirect URLs.
- Exchange authorization codes for tokens.
- Return final tokens, cookies, expiration data, and diagnostics.

## Tests

The system should test that the Azure B2C flow can be replicated.

## Success Criteria

The feature is complete when:

- The engine can execute a detected Azure AD B2C Authorization Code + PKCE flow.
- It can parse and submit a detected username/password login form.
- It can extract hidden form fields.
- It can follow redirects.
- It can capture the authorization code.
- It can exchange the code for tokens.
- It can refresh the token using a refresh token.
- It can run entirely against mocked HTTP responses in tests.
- It fails clearly for MFA, JavaScript-required flows, CAPTCHA, and unsupported providers.


## Outstanding Question

Should this engine ever submit a real user password to a live identity provider, or is it intended only for test and stage environments?


