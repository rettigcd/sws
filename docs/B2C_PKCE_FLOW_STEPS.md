# Azure B2C Flow steps.

Purpose: define the steps used by an Azure B2C / Entra ID to perform AuthCode + PKCE code flow.

1. Generate runtime authentication values
Generate state, nonce, code_verifier, and code_challenge.
Initialize cookie and variable stores.
2. Retrieve OpenID Connect discovery metadata (optional)
	- skip if endpoints already supplied
	- Fetch /.well-known/openid-configuration.
	- Determine authorization, token, logout, and JWKS endpoints.
3. Send authorization request
	- Redirect or navigate to the /authorize endpoint with PKCE parameters.
4. Receive the login page (optional)
	- If session cookies pre-supplied for authorization server, skip to 12
	- Establish the initial authentication session.
	- Receive anti-forgery tokens, session cookies, and hidden form fields.
5. Submit user credentials
	- Post username and password to the identity provider.
6. Handle password reset flow (optional)
	- If the user selects "Forgot Password" or the policy requires it, execute the password reset journey.
7. Handle account sign-up flow (optional)
	- If the user chooses to create an account, execute the sign-up journey.
8. Perform email or phone verification (optional)
	- Complete any verification steps required by the B2C policy.
9. Collect additional profile information (optional)
	- Gather required attributes such as name, country, or preferences.
10. Display and accept terms or consent (optional)
	- Accept terms of service, privacy notices, or application consent screens.
11. Complete the user journey
	- The B2C policy finishes successfully and prepares the authentication result.
12. Redirect back to the client application
	- Return an authorization code to the configured redirect URI.
13. Validate the authorization response
	- Verify state and extract the authorization code.
14. Exchange the authorization code for tokens
	- Send a request to the /token endpoint using the original code_verifier.
15. Receive access, ID, and refresh tokens
	- Store tokens and expiration information.
16. Validate returned tokens (optional)
	- Verify issuer, audience, nonce, signatures, and expiration times.
17. Create the authenticated session
	- Persist tokens, cookies, claims, and any session metadata required by the application.
18. Refresh access tokens using the refresh token (optional, post-authentication lifecycle step)
	- Renew expired access tokens without requiring the user to sign in again.