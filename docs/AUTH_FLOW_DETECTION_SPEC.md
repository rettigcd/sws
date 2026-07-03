# Feature: Automatic B2C / OpenID Connect / OAuth Detection

## Goal

Add automatic detection and analysis of authentication-related HTTP sessions. A “session” is one request/response pair captured from logs. The application should inspect a sequence of sessions and determine whether any of them are part of a known authentication flow such as:

- OpenID Connect Authorization Code Flow
- OpenID Connect Authorization Code Flow with PKCE
- OAuth 2.0 Authorization Code Flow
- OAuth 2.0 Client Credentials Flow
- OAuth 2.0 Refresh Token Flow
- Azure AD B2C custom policy / user flow
- SAML or other unsupported identity protocols, if identifiable

The feature should produce a structured result that explains the detected flow and provides enough information for a replay/authentication engine to reproduce the flow where possible.

---

## Core Concepts

### Session

A session represents one HTTP request/response pair.

Each session should expose:

- Request URL
- HTTP method
- Request headers
- Request cookies
- Query string parameters
- Form body parameters
- JSON body values, if applicable
- Response status code
- Response headers
- Response cookies / Set-Cookie headers
- Response body, if available
- Redirect location, if present
- Timestamp / sequence order

### Authentication Flow

An authentication flow is a related group of sessions that together perform login, token acquisition, token refresh, or logout.

Example:

1. Application redirects browser to `/authorize`
2. Identity provider returns login page
3. User submits credentials
4. Identity provider redirects back with `code`
5. Application exchanges `code` at `/token`
6. Token endpoint returns `access_token`, `id_token`, and/or `refresh_token`

---

## Detection Requirements

The analyzer should scan all sessions and identify sessions likely related to authentication.

Detection signals include:

### OpenID Connect Discovery

Detect URLs ending in or containing:

- `/.well-known/openid-configuration`
- `/.well-known/oauth-authorization-server`

If present, parse the response JSON and extract:

- `issuer`
- `authorization_endpoint`
- `token_endpoint`
- `jwks_uri`
- `userinfo_endpoint`
- `end_session_endpoint`
- `scopes_supported`
- `response_types_supported`
- `grant_types_supported`
- `code_challenge_methods_supported`
- `claims_supported`

The analyzer should record whether the flow endpoints were discovered from a well-known document or inferred from observed traffic.

---

## Endpoint Detection

Identify common OAuth/OIDC endpoints by URL pattern and parameters.

### Authorization Endpoint

Likely authorization request if:

- HTTP method is `GET` or `POST`
- URL contains `/authorize`
- Query/form contains parameters such as:
  - `client_id`
  - `response_type`
  - `redirect_uri`
  - `scope`
  - `state`
  - `nonce`
  - `code_challenge`
  - `code_challenge_method`
  - `response_mode`
  - `prompt`
  - `login_hint`
  - `domain_hint`

Classify as:

- Authorization Code Flow if `response_type=code`
- Implicit Flow if `response_type` contains `token` or `id_token`
- Hybrid Flow if `response_type` contains `code` plus `token` or `id_token`
- PKCE if `code_challenge` is present

### Token Endpoint

Likely token request if:

- HTTP method is `POST`
- URL contains `/token`
- Body contains:
  - `grant_type`
  - `client_id`
  - `code`
  - `redirect_uri`
  - `code_verifier`
  - `client_secret`
  - `refresh_token`

Classify grant type using `grant_type`:

- `authorization_code`
- `client_credentials`
- `refresh_token`
- `password`
- `urn:ietf:params:oauth:grant-type:device_code`

### Redirect Callback

Detect redirect/callback sessions where:

- Request URL matches a previously seen `redirect_uri`
- Query or fragment-like data contains:
  - `code`
  - `state`
  - `id_token`
  - `access_token`
  - `error`
  - `error_description`

The analyzer should correlate the callback with the original authorization request using:

- `state`
- `redirect_uri`
- sequence order
- client id
- issuer/host

### Azure AD B2C Detection

Detect Azure AD B2C if URLs contain patterns such as:

- `b2clogin.com`
- `login.microsoftonline.com`
- tenant-like domains
- policy names such as:
  - `b2c_1_...`
  - `B2C_1A_...`
- query parameter `p=...`
- path segment containing a policy name

Extract:

- tenant
- policy/user flow
- authority base URL
- authorization endpoint
- token endpoint
- redirect URI
- client ID
- response mode
- response type
- scopes
- PKCE values
- B2C cookies

Common B2C cookies to recognize:

- `x-ms-cpim-*`
- `x-ms-cpim-sso:*`
- `x-ms-cpim-cache:*`
- `x-ms-cpim-csrf`
- `x-ms-cpim-trans`
- `x-ms-cpim-dc`
- `x-ms-cpim-ctx`

These cookies should usually be classified as generated/transient flow state, not pre-known configuration.

---

## Variable Classification

For each detected flow, identify every variable participating in the flow.

Variables include:

- Query parameters
- Form parameters
- JSON body fields
- Request headers
- Response headers
- Cookies
- Set-Cookie values
- Redirect URL parameters
- Tokens
- Claims, if token decoding is supported
- Hidden form fields, if HTML parsing is supported

Each variable should be classified into one of these categories:

### Configuration Variables

Known before the flow starts.

Examples:

- `client_id`
- `redirect_uri`
- `scope`
- `authority`
- `tenant`
- `policy`
- `response_type`
- requested resource/audience
- token endpoint URL
- authorization endpoint URL

### Secret Variables

Known before the flow starts but sensitive.

Examples:

- `client_secret`
- private key material
- client assertion signing material
- certificate thumbprint
- Basic Authorization credentials

The analyzer should detect these and report them.  Do not redact values.

### Generated Per-Flow Variables

Generated by the client or browser at runtime.

Examples:

- `state`
- `nonce`
- `code_verifier`
- `code_challenge`
- correlation IDs
- request IDs
- CSRF values
- transaction IDs

### Server-Generated Variables

Returned by the identity provider.

Examples:

- authorization `code`
- `access_token`
- `id_token`
- `refresh_token`
- server cookies
- session IDs

### Derived Variables

Calculated from other variables.

Examples:

- `code_challenge` derived from `code_verifier`
- `client_assertion` derived from a private key
- redirect callback validation derived from `state`

### Non-Participating Variables

Variables present in requests/responses but not required for reproducing the auth flow.

Examples:

- analytics cookies
- telemetry headers
- browser cache headers
- unrelated app cookies
- tracking IDs
- static asset requests
- UI-only parameters

---

## Required vs Optional Parameters

### Authorization Code with PKCE

Required or usually required:

- `client_id`
- `response_type=code`
- `redirect_uri`
- `scope`
- `state`
- `code_challenge`
- `code_challenge_method`

Often required for OIDC:

- `nonce`
- `openid` scope

Optional/common:

- `response_mode`
- `prompt`
- `login_hint`
- `domain_hint`
- `claims`
- `ui_locales`

### Token Exchange

Required:

- `grant_type=authorization_code`
- `client_id`
- `code`
- `redirect_uri`

Required for PKCE:

- `code_verifier`

Required for confidential clients:

- `client_secret`, Basic auth, or `client_assertion`

Optional/common:

- `scope`
- `client_info`

---

## Flow Correlation

The analyzer should group related sessions into a `DetectedAuthenticationFlow`.

Correlation rules:

1. Match authorization request to callback using `state`.
2. Match callback to token request using authorization `code`.
3. Match token request to token response by sequence and URL.
4. Match discovery document to flow by issuer/host.
5. Match B2C policy by URL path or `p` parameter.
6. Match cookies across requests/responses by cookie name and domain.
7. Prefer exact matches over sequence-based guesses.

If correlation is uncertain, include confidence scores and reasons.

---

## Replay Requirements

The analyzer should output what a replay engine needs to know.

Example replay requirements:

- Use authorization endpoint from discovery document.
- Generate new `state` per run.
- Generate new `nonce` per run.
- Generate PKCE `code_verifier`.
- Derive `code_challenge` using SHA-256 and Base64URL when `code_challenge_method=S256`.
- Preserve configured `client_id`.
- Preserve configured `redirect_uri`.
- Preserve configured scopes.
- Do not reuse observed authorization code.
- Do not reuse observed B2C transaction cookies.
- Token endpoint requires `client_secret`.
- Token endpoint requires `code_verifier`.
- Browser-interactive login may be required.
- MFA or CAPTCHA may prevent full automated replay.

---

## Redaction Rules

Do NOT redact anything. Sensitive values must be displayed.

---

## Warnings and Limitations

Emit warnings for:

- Missing discovery document
- Incomplete flow
- Missing callback
- Missing token exchange
- PKCE mismatch
- Missing client secret
- MFA/CAPTCHA requirements
- Unsafe implicit flows
- Sensitive token exposure

---

## Acceptance Criteria

1. Detect OIDC/OAuth/B2C sessions from logs.
2. Group sessions into authentication flows.
3. Identify flow type.
4. Identify endpoints.
5. Extract configuration values.
6. Distinguish generated versus configured variables.
7. Do not redact sensitive values.
8. Produce replay requirements.
9. Generate warnings.
10. Return a structured result suitable for an authentication engine.

---

## Suggested Implementation Phases

1. Session normalization.
2. Endpoint classification.
3. Flow correlation.
4. Variable extraction.
5. Flow analysis.
6. Result generation.
7. Automated tests.

---

## Architectural Note

There should be 2 components.
1. A detector/analyzer - outputs all parameters needed to replicate the flow.
2. A replayer engine - consumes detector/analyzer output and generates requests.

- The replay engine should consume the generated result object and perform authentication using newly generated runtime values rather than reusing captured secrets, codes, or session artifacts.
- the replay engine uses an abstract http client so that it is easily tested.
