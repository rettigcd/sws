# SNL Standby on vow.app: Flow Analysis

Purpose: record how the *new* SNL Standby ticketing site works (vow.app, replacing the old Qudini
booking widget), so a script equivalent to `scripts/QudiniTickets.cs` can be written for it.
This is written for a future session or another AI with no memory of the investigation.

Sources: two Fiddler captures in `saz/snl_sep_24/`, both taken on 2026-09-24 (Thursday) shortly
after the 10:00 AM ET opening, when both shows were already full.

- `snl_sep_24_part_1.saz`: the public landing/listing flow (14 exchanges after stripping)
- `snl_sep_24_part_2.saz`: the registration flow, ending in "Sorry, registration is now closed" (16 exchanges after stripping)

A second week's opening (2026-10-01) was recorded by a browser capture and a script log. What they changed or added is in
**section 11**; where it contradicts an earlier section, section 11 is newer.

Terminology: as in the rest of this repo, an "exchange" is one HTTP request/response pair.
Exchange numbers below are those in the **stripped** captures, and were renumbered when
non-participating exchanges were deleted (part 2's `PUT rsvp` was 28 before stripping, 12 after).
Numbers only identify entries in the current files; they are not stable identifiers.

**Key limitation:** both events were full, so the *successful* registration response was never
captured. Anything about the success path is inferred from the site's JavaScript and step
definitions, and is marked (inferred) below. A capture taken while a show is open is needed to
confirm it.

---

## 1. Summary

1. `https://snlstandby.nbcuni.com/` is a thin page that embeds an iframe to `https://pro.vow.app/public/nbc`.
2. That page polls `GET https://api.vow.app/api/v2/public/by-url/nbc/events` every 20 seconds. Each event has a `status` of `open`, `coming_soon` or `closed`. When `open`, the page shows a "Register Now" button linking to the event's `register_url`.
3. `register_url` is `https://go.vow.app/event/{event_uuid}/journeys/{journey_id}`. This is a separate single-page app. It loads the journey definition with `load-for-visitor`, walks the visitor through steps (landing, form, confirmation or closed), and submits the form with `PUT /api/v2/events/{event_uuid}/attendees/rsvp`.
4. If the event is at capacity, the RSVP `PUT` returns HTTP 422 `{"error":"This event is full.",...}` and the app shows the "Closed" step.
5. No login is required. `GET /api/auth/user` returns 401 and the flow proceeds anyway.

This is a much smaller flow than the old Qudini one (see `docs/SNL_TICKET_FLOW_SPEC.md`): no templates,
scripts, or session registration are needed to reach the booking call, so far as the capture shows.

---

## 2. Hosts

### Participating

| Host | Role |
|---|---|
| `snlstandby.nbcuni.com` | NBC entry page; contains the iframe to `pro.vow.app` |
| `pro.vow.app` | Nuxt front end for the listing page (`/public/nbc`) plus `_nuxt/*.js` bundles |
| `go.vow.app` | Registration single-page app for one event/journey |
| `api.vow.app` | Backend API for both front ends (Laravel/PHP 8.3 behind Cloudflare, per response headers) |
| `ws-us2.pusher.com` | Pusher WebSocket; gives the `X-Socket-ID` that `api.vow.app` requests carry |

### Not participating (seen in captures, safe to ignore)

| Host | What it is |
|---|---|
| `rs.fullstory.com`, `edge.fullstory.com` | FullStory session-replay collector and script. See section 9 for the bot-check question. |
| `cdn.growthbook.io` | Feature-flag stream; its Origin/Referer was `vsco.co`, an unrelated site |
| `c.pki.goog` | Google Trust Services certificate revocation list download (browser/OS TLS validation) |
| `content-autofill.googleapis.com` | Chrome form-autofill vote |
| `waa-pa.clients6.google.com`, `play.google.com`, `signaler-pa.clients6.google.com`, `docs.google.com`, `beacons.gcp.gvt2.com` | Google Drive/Docs/Play background traffic from other browser tabs |
| `odin.adobe.com`, `learnplaylistservice.adobe.com`, `aepxlg.adobe.com`, `cc-cdn.adobe.com`, `utut-service.adobe.com` | Adobe app background traffic |
| `fonts.googleapis.com`, `fonts.gstatic.com` | Font hosts. Referenced in page bodies (Inter) but never requested in the captures. Would be non-participating. |

There were no "unsure" hosts left after checking.

### Technology stack (from response headers and HTML)

Three different technologies are involved. They can be told apart from the response headers and HTML of the pages themselves.

| Layer | Host | Technology | Evidence |
|---|---|---|---|
| Entry page | `snlstandby.nbcuni.com` | ASP.NET on IIS (NBC's own site) | `Server: Microsoft-IIS/10.0`, `X-Powered-By: ASP.NET`. The page is a static 138-byte file (`Last-Modified: 2026-09-17`) containing only the iframe. |
| Front ends | `pro.vow.app`, `go.vow.app` | Nuxt 2 (Vue.js framework) with Vuetify UI components | `data-n-head-ssr` on `<html>`, `data-n-head="ssr"` on meta tags, `vuetify-theme-stylesheet` and `v-application` styles, a page title of "Pro, VOW". `go.vow.app` also sends `x-powered-by: Nuxt`. The `pro.vow.app` page is server-rendered and about 985 KB. |
| Backend API | `api.vow.app` | Laravel (PHP 8.3) with Laravel Sanctum, behind Cloudflare and an AWS load balancer | See the next subsection. **Cloudflare blocks non-browser User-Agents** (403 "Error 1010"); see 7.6. |

Consequence: the HTML pages are JavaScript apps and all the data comes from `api.vow.app`. A script never needs to parse the HTML. Only the JSON
API matters.

### Backend framework: Laravel

`api.vow.app` is a **Laravel** application (confirmed by the `laravelSanctum` cookie below; the rest of the evidence agrees). Laravel is a widely used open-source PHP web framework
that provides routing, sessions, cookie encryption, CSRF protection, request validation and request throttling
out of the box. Knowing this explains several behaviors seen in the captures and predicts others.

Evidence:

- The `pro.vow.app` page response sets the cookie `auth.strategy=laravelSanctum`. The front end names its own auth mechanism there.
  Laravel Sanctum is Laravel's official package for API tokens and cookie-based single-page-app authentication.
- Response header `x-upstream: unix:/run/php/php8.3-fpm.sock` shows a PHP 8.3 FastCGI backend.
- Cookies named `XSRF-TOKEN` and `vow_session`. `XSRF-TOKEN` is Laravel's CSRF cookie name, and Laravel session cookies are
  named `<app>_session` by default.
- Cookie values are base64 JSON containing `iv`, `value`, `mac` and `tag`. That is the format of Laravel's encrypted cookies.
- The 401 body `{"message":"Unauthenticated."}` is Laravel's stock unauthenticated JSON response.
- The 422 status for a failed request is Laravel's convention for validation-style failures.
- The `x-ratelimit-limit` and `x-ratelimit-remaining` headers are what Laravel's `throttle` middleware emits (see 5.4).
- The CORS allow-list includes `X-CSRF-TOKEN` and `x-xsrf-token`, the two header names Laravel reads for CSRF.

What that means for a script:

- **Cookies carry the session.** Laravel keeps the visitor's session in the encrypted `vow_session` cookie. A script must use a cookie
  container so these are sent back, and must not modify their values (they are signed with a MAC).
- **Sanctum.** In its single-page-app mode Sanctum expects the client to first call `GET /sanctum/csrf-cookie`, which sets the `XSRF-TOKEN` cookie, and then
  send that value back in an `X-XSRF-TOKEN` header. That call is not in either capture (the cookie was already set by `load-for-visitor`), so this
  API is evidently not using that handshake for anonymous visitors. The `GET /api/auth/user` 401 seen in part 2 is consistent with Sanctum's user endpoint.
- **CSRF.** Laravel normally rejects state-changing requests without a matching `X-XSRF-TOKEN` header (the URL-decoded `XSRF-TOKEN` cookie).
  The browser's `PUT rsvp` sent no such header and still reached capacity validation, so CSRF is evidently not enforced on this API
  route. If a later capture shows a 419 ("CSRF token mismatch"), that is the cause: add `X-XSRF-TOKEN` with the URL-decoded cookie value.
- **Throttling.** Laravel's rate limiter counts requests per key (by default the client IP, or the user id when logged in) per time window, returns
  429 with `Retry-After` when exceeded, and sends `x-ratelimit-*` headers. See 5.4.
- **Validation errors.** Laravel returns 422 with a JSON body. Here the body was a custom `{"error":..., "capacity_full":true,...}` and not
  Laravel's default `{"message":...,"errors":{...}}` shape, so a real validation failure (for example a bad email) may look different from "event full". Handle both.

---

## 3. Important URLs

### Page URLs

| URL | Notes |
|---|---|
| `https://snlstandby.nbcuni.com/` | Entry point. Response body contains `<iframe src="https://pro.vow.app/public/nbc" ...>`. |
| `https://pro.vow.app/public/nbc` | Listing page (Nuxt SPA). Loads `_nuxt/3febb21.js`, `7dfb2c2.js`, `74cbf5e.js`, `5596a08.js`, `bcbcf1d.js` (hashes change on redeploy). |
| `https://go.vow.app/event/33dd58bf-d83a-4b8d-9929-559907ff8e34/journeys/1367` | Registration page, **Dress Rehearsal** (2026-09-26 20:00 UTC). |
| `https://go.vow.app/event/6c9af77d-e6b5-44dd-bde0-f5eef4e7986e/journeys/1364` | Registration page, **Live Show** (2026-09-26 23:30 UTC). Its `register_url` was seen in part 1; the registration flow itself was only captured for the Dress Rehearsal. |

### API endpoints (all on `https://api.vow.app`)

| Method | Path | Purpose | Seen in |
|---|---|---|---|
| GET | `/api/v2/public/by-url/nbc/events` | List the two SNL events with status and `register_url` | part 1, part 2 |
| GET | `/api/auth/user` | Auth check. 401 `{"message":"Unauthenticated."}` when anonymous. Not required. | part 2 |
| GET | `/api/v2/events/{event_uuid}/journeys/{journey_id}/load-for-visitor` | Journey definition (steps, actions, capacity, counts) | part 2 |
| POST | `/api/v2/events/{event_uuid}/journeys/{journey_id}/log-interaction` | Reports which step the visitor is on. Body `{"step_id":N,"action_id":"N"\|null,"session":<ms epoch>}`. Response is 200 with a non-JSON/HTML content type. Likely analytics only. | part 2 |
| PUT | `/api/v2/events/{event_uuid}/attendees/rsvp` | **The registration call.** | part 2 |
| GET | `wss://ws-us2.pusher.com/app/a7fadfa6cfe13872810f?protocol=7&client=js&version=8.4.0&flash=false` | Pusher WebSocket (101). (`version=8.0.1` on the part 1 listing page.) | part 1, part 2 |

Every request from a browser is preceded by a CORS `OPTIONS` preflight (204) because the request
sends `X-Socket-ID`. A non-browser client does not need to send preflights.

### Known IDs (as of 2026-09-24; may change per show/week)

| Item | Dress Rehearsal | Live Show |
|---|---|---|
| Event uuid | `33dd58bf-d83a-4b8d-9929-559907ff8e34` | `6c9af77d-e6b5-44dd-bde0-f5eef4e7986e` |
| Journey id | 1367 | 1364 |
| Journey name | `STANDBY - SNL, Dress Rehearsal` | (not captured) |
| Capacity | 305 | 305 |
| Signups at capture | 306 (`attending_count`) | 309 |
| Registration window | 2026-09-24 10:00 to 11:00 ET (`opens_at`, `closes_at`) | same |
| Location | 30 Rockefeller Center, 49 West 49th Street, New York NY 10019 | same |

New shows are announced weekly ("opens Thursdays at 10:00 AM EST before each show"), so the uuids and
journey ids will change every week. A script should discover them from the listing endpoint and
not hard-code them.

The Pusher app key `a7fadfa6cfe13872810f` and FullStory OrgId `13J8S4` are vow.app's, not ours.

---

## 4. The listing call

`GET https://api.vow.app/api/v2/public/by-url/nbc/events`, no auth, cookie-free (only an `AWSALBCORS`
load-balancer cookie was sent). Request headers in the browser: `Accept: application/json, text/plain, */*`,
`Origin: https://pro.vow.app`, `Referer: https://pro.vow.app/`, `X-Socket-ID: <pusher id>`.

Response, abridged:

```json
{"events":[{
  "uuid":"33dd58bf-d83a-4b8d-9929-559907ff8e34",
  "name":"SNL Standby: Dress Rehearsal",
  "timezone":"US/Eastern",
  "starts_at":"2026-09-26T20:00:00.000000Z",
  "location":{"uuid":"...","name":"30 Rockefeller Center","address":"49 West 49th Street","city":"New York","state_region_code":"NY","postal_code":"10019", "...":"..."},
  "opens_at":"2026-09-24T10:00:00-04:00",
  "closes_at":"2026-09-24T11:00:00-04:00",
  "journey_id":1367,
  "register_url":"https://go.vow.app/event/33dd58bf-d83a-4b8d-9929-559907ff8e34/journeys/1367",
  "capacity":305,
  "attending_count":306,
  "theme":{"...":"..."},
  "status":"closed"
}, { "...second event, same shape..." }]}
```

Front-end logic (from `pro.vow.app` bundle, exchange 7 of part 1): the events poll runs on mount and
every 20,000 ms. A card shows:

- `status === "open"` and `register_url` set: a **"Register Now"** link to `register_url`
- `status === "coming_soon"`: "Coming Soon" label
- otherwise: greyed "closed" button

Observed status was `closed` for both events while `attending_count` exceeded `capacity`. It is not
known whether `closed` here is derived from capacity, from the time window, or both (`closes_at` was
still an hour away), or what the exact status string is when open. `"open"` and `"coming_soon"`
are taken from the front-end code, not seen in data.

**Update 2026-10-01 (section 11.2):** `coming_soon` and `open` were both seen. In all three states the list now has only `uuid`,
`name`, `starts_at`, `theme`, `status` and (only while open) `register_url`. There is no `journey_id`, `capacity`, `attending_count`,
`opens_at` or `closes_at`; the journey id must be taken from the end of `register_url`.

---

## 5. The registration page

### 5.1 Page load

1. `GET https://go.vow.app/event/{uuid}/journeys/{id}` returns the SPA shell.
2. The SPA calls `GET /api/auth/user` (401, ignored) and `GET .../load-for-visitor` (200, about 84 KB gzip-decoded).
3. It opens the Pusher WebSocket and reads the socket id from the `pusher:connection_established` frame
   (see section 5.6 for what Pusher is used for).

Cookies set by `api.vow.app` responses, all on `domain=.vow.app`, `secure`, `samesite=lax`, 15-day expiry:

| Cookie | Notes |
|---|---|
| `XSRF-TOKEN` | Laravel CSRF cookie (JS-readable) |
| `vow_session` | Laravel session (httponly) |
| a long random-named cookie (e.g. `fnCesXodIt7xE3YZGJyGDJTKTZv594DjELbG9bBG`, another name on the listing calls) | Laravel-encrypted, httponly. Name differs by call and appears to be a per-guard or per-app cookie. |
| `AWSALB`, `AWSALBCORS` | AWS load-balancer stickiness, 7-day expiry |

The browser sent these back on later calls. Its `PUT rsvp` did **not** send an `X-XSRF-TOKEN` header
(only the `XSRF-TOKEN` cookie), and the server still got as far as capacity validation, so CSRF
header enforcement is not evident (inferred; unconfirmed).

### 5.2 `load-for-visitor` response

Top-level keys: `journey`, `attendees`, `event`, `me`, `is_over_capacity`.

In the capture: `attendees` was `[]`, `me` was `null`, `is_over_capacity` was `true`, and `event.status`
was `"draft"` (this is an internal event state; do not assume it means anything about registration).

`journey` (metadata, abridged):

```
id 1367, name "STANDBY - SNL, Dress Rehearsal", is_public 1, is_auto_approved 0,
capacity 305, capacity_step_id 6110, signup_count 306, queued_count 0,
queue_when_at_capacity false, auto_close_at null, auto_close_capacity null,
assign_registration_sequence true, closed_reason null,
link "https://go.vow.app/event/{uuid}/journeys/1367"
```

`journey.steps` and `journey.actions` define a small state machine:

| Step id | Name | Type | Role |
|---|---|---|---|
| 6107 | Landing Page On-screen | `page` (root) | Explains the process; "BOOK STANDBY RESERVATION" button |
| 6108 | RSVP | `rsvp` | The registration form. Options: `max_plus_ones: 1`, `included_fields: []`, `rsvp_show_no_button: false`. `step_group: "rsvp-5773"`. |
| 6109 | RSVP Yes on-screen Confirmation | `page` | Success: "Your party has been placed in our standby queue!" plus a "Standby Reservation Booking Number" |
| 6110 | Closed on-screen | `page` | Failure: "SNL Standby Booking is now CLOSED ... reached capacity" |

| Action id | from | to | label | function | condition |
|---|---|---|---|---|---|
| 4741 | 6107 | 6108 | Continue | `to` | none |
| 4742 | 6108 | 6109 | YES | `rsvp_yes` | `total_attendees_gt` 300 |
| 4743 | 6108 | 6110 | "Nr. of Att >300" | `condition` | `total_attendees_gt` 2000 |

The conditions look odd (300 and 2000 against a capacity of 305) and their exact semantics were not
worked out. `capacity_step_id: 6110` is the field the server echoes back when full.

Step HTML (`steps[].content.html`) contains the visible text and links. Notable content, paraphrased:

- Landing: reservation opens Thursdays at 10:00 AM EST before each show; duplicate requests for the same date are not allowed, and only the latest request counts.
- Form: "By clicking SUBMIT I confirm I (and my guest) will be on site at 49 W. 49th Street on Friday at exactly 7:00 PM EST until Saturday 12:01 AM EST." (Dress Rehearsal text.)
- The step HTML embeds direct links between steps (see 5.3).

### 5.3 URLs that reveal the next step

The address bar does not change while the visitor moves between steps (single-page app). The step HTML
does contain deep links using a `step` query parameter:

```
https://go.vow.app/event/{uuid}/journeys/{id}?attendee=-&step=6108     (landing links to the form)
https://go.vow.app/event/{uuid}/journeys/{id}?attendee=-&step=6109     (form links to the confirmation)
```

The `-` for `attendee` looks like a placeholder, likely replaced by an attendee id after a successful
RSVP (inferred). The steps' own `id`s are per-journey; a new week's journey will have new step ids,
so read them from `load-for-visitor` rather than hard-coding.

### 5.4 The RSVP call

```
PUT https://api.vow.app/api/v2/events/{event_uuid}/attendees/rsvp
Accept: application/json
Content-Type: application/json
Origin: https://go.vow.app
Referer: https://go.vow.app/
X-Socket-ID: <pusher socket id>
Cookie: XSRF-TOKEN=...; vow_session=...; <third laravel cookie>; AWSALB=...; AWSALBCORS=...; fs_uid=...; fs_lua=...
```

Body (138 bytes in the capture; the name and email are placeholders here, not the captured values):

```json
{"rsvp":true,"plus_ones":0,"me":null,"journey":1367,"group_id":null,
 "first_name":"<first>","last_name":"<last>","email":"<email>"}
```

- `plus_ones`: the number of guests beyond the registrant. The step allows at most 1, so this is the equivalent of group size (0 or 1).
- `journey`: the journey id (integer).
- `me` and `group_id` were `null` for a first-time anonymous visitor.
- No phone number, no other fields in this journey (`included_fields: []`).
- Only three personal fields: first name, last name, email.

Response when full (observed):

```
HTTP/1.1 422 Unprocessable Entity
x-ratelimit-limit: 10
x-ratelimit-remaining: 9
{"error":"This event is full.","capacity_full":true,"capacity_step_id":6110}
```

#### Rate limit

The RSVP response carries Laravel throttle headers:

| Header | Observed | Meaning |
|---|---|---|
| `x-ratelimit-limit` | `10` | Requests allowed per window on this endpoint |
| `x-ratelimit-remaining` | `9` | Requests left after the one that was just counted |

What is **known**: the limit is 10, and one request had been counted when the header was read. The headers appeared only on this call, not on
`load-for-visitor` or the listing calls, so those are either not throttled or use a different limit.

What is **not known** (not in any capture):

- The window length. There is no `Retry-After` or reset header. (Tests on 2026-09-24, section 7.6: `x-ratelimit-remaining` fell 9, 8, 7, 6, 5 over five RSVPs sent over a few minutes, with no reset seen, and the counter was shared across separate runs with different cookies.) Laravel's own default is 60 requests per minute, so a limit of 10 is a custom choice
  and the window could be a minute or something else.
- What the limit is keyed on. Laravel defaults to the client IP (or the user id when logged in). The 2026-09-24 tests showed it is **not** per cookie/session (separate cookie jars shared one counter), so for an anonymous visitor it is most likely the IP address.
- Whether requests that fail still count: a 422 does count (remaining fell on every 422). A Cloudflare 403 (section 7.6) did not.
- What happens when the limit is exceeded. Laravel's default is HTTP 429 with a `Retry-After` header. It was never triggered.

**Any retry mechanism must respect this limit.** Requirements for a retry, hedge or polling strategy against the RSVP endpoint:

- Budget: the total number of RSVP requests, including retries and hedged duplicates, must stay under 10 per window. Treat 10 as a hard ceiling, and
  assume a smaller effective number if anything else (another browser tab, another script, other people on the same IP) shares the key.
- Do not hedge or fire duplicates of the RSVP (as `Step1_WithRetry` in `scripts/QudiniTickets.cs` does for the Qudini index page). Duplicates count against the limit and can lock out the attempt that would have succeeded.
- Read `x-ratelimit-remaining` on every response and stop or slow down when it is low. Do not assume it started at 10.
- Handle 429: honor `Retry-After` when present, and back off. Do not retry immediately.
- Retry only failures that are safe to retry (network errors, 5xx, 429 after the wait). Do not retry 422 "event is full" quickly. It only wastes the allowance, and the
  event is unlikely to gain a seat (unless someone cancels).
- Other endpoints (`load-for-visitor`, the listing) showed no limit, but that is not proof. Poll them modestly.
- Test the window in a safe way: while events are full, send several RSVPs and watch `x-ratelimit-remaining` count down and reset (each gets 422 and takes no seat).

Response on success: **not captured** (inferred: HTTP 200 with the attendee record and a pointer to step
6109, and the SPA then logs an interaction for step 6109 and shows the confirmation with a
"Booking Number").

### 5.5 `log-interaction`

Body examples from the capture, in exchange order:

```
{"step_id":6110,"action_id":null,"session":1790262433904}   (first, right after load)
{"step_id":6107,"action_id":null,"session":1790262433904}
{"step_id":6108,"action_id":"4741","session":1790262433904} (clicked Continue on landing)
   ... user fills form, PUT rsvp -> 422 ...
{"step_id":6110,"action_id":null,"session":1790262433904}   (after the full response)
```

`session` is a client-generated millisecond epoch, constant for the whole page visit. `action_id`
is the action clicked to reach the step (a string) or null. It looks like analytics and the RSVP
call does not appear to depend on it, but that was not tested (a replay that skips these is the way to test).
The first `6110` entry before the landing page is unexplained. It may be the SPA rendering the closed
step immediately because `is_over_capacity` was already true, before the visitor clicked anything (a guess).

---

### 5.6 Pusher (WebSocket) traffic

**What Pusher is.** Pusher is a hosted publish/subscribe service that pushes real-time messages from a server to browsers over a WebSocket. vow.app runs
it through **Laravel Broadcasting** (server side) and **Laravel Echo** (the JavaScript client). The app key `a7fadfa6cfe13872810f` (cluster `us2`) is public: it is in the page's JS.

**What it is used for here.** In these captures, almost nothing:

- Two things happen on connect and every request:
  1. **`X-Socket-ID` header.** The front-end bundle (`pro.vow.app`, part 1) installs an axios interceptor that adds `X-Socket-ID: <socket id>` to *every* API request
     (`e.headers["X-Socket-ID"]=o.socketId()`). Laravel uses this header so that, when it broadcasts an event caused by a request, it can skip the sender
     ("broadcast to others"). It is not a credential. The value comes from the `pusher:connection_established` message below. Before the socket connects it is the string `undefined`
     (seen on the first listing call).
  2. **Real-time page updates.** Laravel Echo is set up (`broadcaster: "pusher"`, `authEndpoint`-style authorizer that would `POST /api/broadcasting/auth` with
     `{socket_id, channel_name}` for private channels), so the app *can* subscribe to channels and receive events (for example, live availability changes). In these two
     captures no channel was ever subscribed to: no `pusher:subscribe` message and no `POST /api/broadcasting/auth` appear. (Echo is initialized for anonymous visitors too,
     since their requests carried `X-Socket-ID`. The source also re-runs the setup when the auth `loggedIn` state changes. So private channels are presumably for logged-in
     organizers on the admin side, not for public registrants. That is an inference.)
- The socket only exchanges keep-alive messages, as decoded below.

**Decoded frames (part 2, one connection, about 14 minutes):**

| Direction | Message | Meaning |
|---|---|---|
| server to client (first) | `{"event":"pusher:connection_established","data":"{\"socket_id\":\"1677674.4468990\",\"activity_timeout\":120}"}` | Handshake. Gives the socket id used in `X-Socket-ID`; the connection times out after 120 s of silence. |
| client to server (about every 2 min) | `{"event":"pusher:ping","data":{}}` | Application-level keep-alive |
| server to client | `{"event":"pusher:pong","data":"{}"}` | Reply |
| server to client / client to server | WebSocket ping (opcode 9) and pong (opcode 10) frames, 2 to 6 bytes | Protocol-level keep-alive every minute |

Part 2 had 1 `connection_established`, 7 `pusher:ping`, 7 `pusher:pong`, 9 protocol pings and 9 protocol pongs. Part 1 was the same pattern over a much longer
connection (about 48 minutes): 24 application pings and 30 protocol pings, and no other events. Socket ids: part 1 `1692194.4180406`, part 2 `1677674.4468990`.

**Implications for a script.**

- `X-Socket-ID` is the *only* thing from Pusher that the HTTP calls use. A script has two choices: open the WebSocket to the same URL and read
  `socket_id` from the first message (faithful to the browser), or omit or fake the header. The server returned 200 for the literal `undefined` on the first
  listing call, which suggests it is not validated, but that was not tested on the RSVP.
- If a script does connect, it must answer the server's keep-alive or the connection drops after `activity_timeout` (120 s). A WebSocket client library handles protocol pings automatically.
- No events were received in the captures, so nothing in a Pusher message was needed to complete the flow. A script that does not care about live updates can ignore the socket after reading the id.
- If a future capture shows `pusher:subscribe` and `/api/broadcasting/auth`, that would indicate the page uses real-time availability, which might be useful to detect an opening.

---

## 6. Observed timeline (part 2, times UTC on 2026-09-24)

| Time | Event |
|---|---|
| 15:07:13 | Page loads; `auth/user` 401; `load-for-visitor` 200 |
| 15:07:13 | Pusher connect; first `log-interaction` calls |
| about 15:07:13 to 15:07:31 | Visitor reads landing page, clicks Continue, types into form |
| 15:07:32 | `PUT rsvp` returns 422 "event is full" (about 19 seconds after load) |
| 15:07:37 | Listing poll from `pro.vow.app` (another tab) |

---

## 7. Notes for writing a script like `scripts/QudiniTickets.cs`

`scripts/QudiniTickets.cs` is a single-file C# script (`dotnet run scripts/QudiniTickets.cs -- --site snl|icecream ...`)
that books through the old Qudini widget using only the five required steps (1, 4, 7, 9, 13 of
`docs/SNL_TICKET_FLOW_SPEC.md` section 5). `app/Qudini/SnlTicketSession.cs` and `IceCreamTicketSession.cs` are
the older, longer versions that replay about 60 steps. The vow.app flow is far shorter than either.

### 7.1 What to keep from `QudiniTickets.cs`

- **Shape:** top-level script with a `Context` class, one `StepN_...` async method per HTTP call, `SendAsync`
  (throws on non-2xx, prints one line) and `SendOptionalAsync` (logs and continues) helpers, and small DTO classes with
  `[JsonPropertyName]`.
- **CLI/dry-run design:** default is a dry run that sends only the read-only steps and prints the booking request; `--submit`
  sends it; `--list` prints the events and stops; `--event`, `--title` and the default "first event not passed with
  seats" pick the event; `UserConfig` entries plus `--config INDEX` hold per-user data; `--group-size` overrides it.
- **Timed start:** `GetNextThursday10Am(msDelay)` and `WaitUntilRunTime()` count down to just after 10:00 AM Thursday.
  The vow.app window is the same (10:00 to 11:00 ET), so this carries over unchanged.
- **Cookie container:** `HttpClientHandler { CookieContainer, UseCookies = true, AutomaticDecompression = All }`. This matters
  more here, since the Laravel cookies come from the first API calls.
- **Browser headers:** `AddBrowserHeaders` (User-Agent, `sec-ch-ua*`, `Accept-Language`) and the `JsonGet`/`JsonPost`
  helpers. For vow.app the `Origin` and `Referer` differ (`https://go.vow.app`), and `Sec-Fetch-Site` is `same-site`, not `same-origin`.
- **Hedged Step 1 (`Step1_WithRetry`):** exists because the Qudini index page returned 5xx/hung. Nothing in the vow.app
  captures showed a 5xx, so it is probably unnecessary for the first version. Do not reuse it for the RSVP (see the rate limit in 5.4).
- **Dummy-data caution:** the script's `configs` array holds a real-looking name, email and phone number. Any vow.app
  version should take these from user config or arguments, not commit them.

### 7.2 Step mapping: Qudini script to vow.app

| Qudini step (`QudiniTickets.cs`) | vow.app equivalent | Notes |
|---|---|---|
| Step 1: `GET bookings-us.qudini.com/booking-widget/events/{series}` (sets `Q-BW-USER-ID`, `Q-BW-SESSION-ID` cookies) | `GET go.vow.app/event/{uuid}/journeys/{journey_id}`, then `load-for-visitor` (sets Laravel cookies) | Cookies are not read back by name; the cookie jar just carries them. No user/session ids to extract. |
| Step 3: register widget session (`--analytics`) | none required (`log-interaction` is the closest analytics) | Optional. |
| Step 4: series settings (max group size, attribution answer, phone state) | `load-for-visitor`: `journey.capacity`, `signup_count`, `is_over_capacity`, and `steps[type=rsvp].options.max_plus_ones` | Form fields here are fixed: first name, last name, email. No phone, no attribution. |
| Step 7: events list (numeric `id`, `identifier`, `slotsAvailable`, `maxGroupSize`) | `GET api.vow.app/api/v2/public/by-url/nbc/events` (`uuid`, `journey_id`, `capacity`, `attending_count`, `status`) | Seats free = `capacity - attending_count`. Selectors: match on `name`, e.g. contains "Dress" or "Live". `hasPassed` has no direct equivalent; use `starts_at` or `status`. |
| Step 9: create event booking session | `POST .../log-interaction` (optional) | Not shown to be required. |
| Step 13: `POST .../booking-widget/series/{series}/event/book` | `PUT api.vow.app/api/v2/events/{uuid}/attendees/rsvp` | Body in 5.4. Qudini returns `refNumber`; the vow.app success shape is unknown. |
| Steps 8, 10, 12, 14: analytics | `log-interaction` calls | Optional. |

`GetGroupSizeToRequest` becomes `plus_ones = min(GroupSize - 1, max_plus_ones)`, since vow.app counts *additional* guests (0 or 1 here),
whereas Qudini's `groupSize` is total people. This mapping (`groupSize` = `plus_ones` + 1) is inferred, not seen in a capture.

### 7.3 Minimal call sequence the captures support

1. **Discover the event.** `GET api.vow.app/api/v2/public/by-url/nbc/events`. Pick by `name`. Read `uuid`, `journey_id`, `status`,
   `opens_at`. A browser only shows the register button when `status` is `open`.
2. **Open the journey.** `GET go.vow.app/event/{uuid}/journeys/{journey_id}` (optional) and
   `GET api.vow.app/.../journeys/{journey_id}/load-for-visitor`. This sets the Laravel cookies (`XSRF-TOKEN`,
   `vow_session`, third cookie) that later calls send back. Read `is_over_capacity`, `capacity_step_id`, `max_plus_ones`.
3. **(Optional) mimic the browser.** Pusher connect, and `log-interaction` calls (landing, then the Continue action to step 6108).
   Unknown whether the server requires any of them.
4. **Submit.** `PUT api.vow.app/api/v2/events/{uuid}/attendees/rsvp` with the body in 5.4.
5. **Interpret the result.** 422 with `capacity_full: true` means full. Anything else needs the success capture to define.

### 7.4 Design points

- **Discover, do not hard-code.** Event uuids, journey ids and step ids change every week. Take them from the listing and `load-for-visitor`.
- **Send** `Origin: https://go.vow.app` and `Referer: https://go.vow.app/` on the api.vow.app calls, as the browser did. The API's CORS policy allows only
  the vow.app front ends' origins, and server-side checks on them are unknown. Send `Accept: application/json`, and `Content-Type: application/json` on the PUT.
  No CORS preflights are needed from a non-browser client.
- **Timing matters.** The show was full within minutes (`attending_count` exceeded `capacity` in the capture). Poll the listing just before 10:00 and
  go straight to the RSVP.
- **Possible early calls.** `GET /api/auth/user` (always 401 for an anonymous visitor, no event in the URL) and the Pusher connection (the socket id
  comes from Pusher and is not tied to the event) could be sent while polling for the opening, to save time after it. `scripts/VowTickets.cs --mode 3`
  still sends them after the opening, in the browser's order. If moved earlier: the Pusher socket closes after 120 s without traffic (5.6), so connect
  less than ~2 minutes before the RSVP or keep it alive with `pusher:ping`. Untested either way.
- **Rate limit.** The RSVP endpoint reports `x-ratelimit-limit: 10` (Laravel throttle; window and key unknown). Every retry, hedge or duplicate counts against it,
  so any retry mechanism must be designed around that budget. See "Rate limit" in section 5.4.
- **PII.** The captures contain a real name and email in the RSVP body. Do not commit them.

### 7.5 Open items to resolve before the script is complete

1. The success response of `PUT rsvp` (needs a capture while a show is open).
2. Whether `X-Socket-ID` must be a real Pusher id (see 5.6). It was sent as the literal string `undefined` on the first listing call and the server still returned 200, so it may not be validated.
3. Whether `log-interaction` calls and the Pusher connection are required for an event that has room. Not checked for a full event (7.6); untested otherwise.
4. Whether cookies are required for an event that has room. The listing needs none, and a full event's RSVP ignored their absence (7.6). Untested otherwise.
5. ~~Exact `status` values in the listing when a show is open or upcoming.~~ Resolved 2026-10-01: `coming_soon`, then `open`, then `closed` (section 11.2).
6. Whether there is any bot detection (see section 9).
7. Whether `plus_ones` is additional guests (assumed) or total group size.

### 7.6 Tests of what is required (run 2026-09-24)

Question: are the browser-mimicking calls (page view, auth check, Pusher/`X-Socket-ID`, `log-interaction`), cookies, and `Origin`/`Referer` needed for the RSVP?

Method: with both events full, send the real RSVP (`PUT .../attendees/rsvp`, placeholder name/email, Dress Rehearsal, journey 1367) with different things omitted,
and compare the response. A full event answers 422 `{"error":"This event is full.","capacity_full":true,...}` and takes no seat. If omitting something changes the answer,
that thing is checked. Sent from `scripts/VowTickets.cs` (rows A, B) and a scratch Python script (the rest).

| Run | What was sent | Result | `x-ratelimit-remaining` after |
|---|---|---|---|
| B | `VowTickets.cs` default: events list + `load-for-visitor` + RSVP. Mode 2 (no `--mode`), so no `X-Socket-ID`, no page view, no `log-interaction`. Chrome UA, Origin, Referer. | 422 event full | 9 |
| A | `VowTickets.cs --mode 3`: adds page view, auth check, Pusher connect (real socket id), 2 `log-interaction` calls. | 422 event full | 8 |
| C | No cookies at all (no `load-for-visitor`), browser UA, Origin, Referer. | 422 event full | 7 |
| F | Cookies, made-up `X-Socket-ID: 123456.7890123`. | 422 event full | 6 |
| E2 | Browser UA only: no cookies, no Origin, no Referer. | 422 event full | 5 |
| E | Cookies, Origin and Referer removed, **and Python's default User-Agent**. | **403 Cloudflare "Error 1010: Access denied"** | not sent |
| G | Origin/Referer present, Python default User-Agent. | **403 Cloudflare 1010** | not sent |
| H | `GET` events list with Python default User-Agent. | **403 Cloudflare 1010** | n/a |

Conclusions:

1. **A browser `User-Agent` is required, on every endpoint.** Cloudflare (in front of `api.vow.app`) returns 403 with a body pointing to error 1010 ("the site owner has blocked your browser signature")
   for a script's default User-Agent, including on the read-only events list. Origin, Referer, client hints (`sec-ch-ua*`) and `Accept-Language` were **not** needed:
   run E2 sent only `Content-Type`, `Accept` and the Chrome `User-Agent`. An HTTP client that sends no `User-Agent` (as .NET's `HttpClient` does by default) is presumably blocked the same way, so always set one.
2. **None of these were checked before the capacity test:** the page view, auth check, Pusher/`X-Socket-ID` (absent, real, or fake), `log-interaction`, any cookie (run C sent none), `Origin`, `Referer`.
3. **That does not prove they are unneeded for an event that has room.** Capacity is very likely checked first (it is the first failure a full event can produce), so the server may
   validate more after it. The only real proof is an RSVP against an open event, or a capture of one.
4. The RSVP's rate-limit counter was **shared across separate runs and cookie jars** and fell by 1 per 422. It did not reset over a few minutes. Cloudflare 403s did not count.
5. `log-interaction` has a separate, much larger limit: `x-ratelimit-remaining` read 1999/2000 and 1998/2000 on two consecutive calls.
6. Five of the 10 RSVP requests in this window were spent on the tests. Sending the tests again soon would risk a 429.

Consequences for `scripts/VowTickets.cs`: the User-Agent stays a known constant that must not be dropped; the browser-mimicking steps are only sent in `--mode 3`
(not proven required, not proven unnecessary); use `--mode 3` when a real registration matters and you want to copy the browser as closely as possible.

## 8. Data limitations

- **WebSocket frames are in the `.saz`, but not in `*.exchanges.json`.** Fiddler stores them in `raw/NN_w.txt` (part 1: `08_w.txt`, part 2:
  `04_w.txt` in the stripped files). Each frame is preceded by a `Request-Length:`/`Response-Length:`, `ID:` and timestamp header block, followed by the raw
  WebSocket frame bytes. Client frames are masked, and text frames are compressed with permessage-deflate (raw deflate, RSV1 set, no context takeover).
  To decode one: unmask, append `00 00 ff ff`, and inflate with a raw-deflate decompressor (Python: `zlib.decompressobj(-15)`). See 5.6.
- **Response bodies for `api.vow.app` JSON calls are not stored in `*.exchanges.json`.** The file has
  `Body.Length/ContentType/Format` metadata only, and `ResponseText` is populated only for some (text/JS) responses. To read a JSON body, unzip the `.saz` (it is a zip) and read `raw/NN_s.txt` (headers, blank line, body).
  Bodies are often `Transfer-Encoding: chunked` and `Content-Encoding: gzip`.
- **The app's output files** are `<name>.exchanges.json`, `<name>.sources.json` and `<name>.auth.json`.
  `auth.json` reports no flows for these captures, because there is no OIDC/OAuth flow here (it is anonymous).
- The capture contains a real person's name and email (the RSVP request body, the FullStory user id).
  Treat the `.saz` and derived files as personal data.

---

## 9. Open question: bot detection via FullStory

FullStory (`rs.fullstory.com`) is a session-replay tool included by vow.app. It is Not participating
in the sense that no API call needs a value from it, but the following was observed and left open:

- FullStory sets a first-party cookie, `fs_uid=#13J8S4#<userId>:<sessionId>:<ts>::<n>####/<expiry>`, on the
  `vow.app` domain, so the browser sends it to `api.vow.app` automatically. It was present on
  `auth/user`, `load-for-visitor`, and `PUT rsvp`, and absent on `log-interaction`.
  It therefore gives the vow.app backend a FullStory session id on the calls that matter.
- The backend could, in principle, ask FullStory's server API whether that session has a real recording
  (mouse movement, typing) and treat a missing or thin one as a bot. Nothing in the capture shows it does. A
  server-to-server call would be invisible to the client, and FullStory is not a bot-detection product.
- Partial result (7.6): RSVPs sent with no cookies at all, so no `fs_uid`, and with no FullStory traffic still got the normal "event is full" answer, not a bot rejection.
  A full event may be refused before any bot check would run, so this weakens the idea but does not rule it out for an event with room.
- Test idea: replay the flow with and without FullStory cookies and see whether the RSVP response differs.
  While events are full both return 422, so this only produces a clear signal when a show is open.

---

## 10. How this was produced (regenerating)

```bash
cd app
dotnet run -- ../saz/snl_sep_24/snl_sep_24_part_2.saz
```

The app writes `<name>.exchanges.json`, `.sources.json`, `.auth.json` next to the `.saz`. Part 1 and
part 2 were first analyzed with all traffic, then non-participating exchanges (section 2) were deleted
in Fiddler and the file re-saved and re-run, so the checked-in captures contain only participating hosts.
A reasonable check after any new capture: group exchanges by `Request.Host` and confirm only the
five participating hosts remain.

---

## 11. The 2026-10-01 opening

Two recordings of the second Thursday opening. Times are Eastern (UTC-4) unless marked UTC.

| Source | What it covers |
|---|---|
| `saz/vow/snl_oct_01/snl_oct_01.saz` | Browser capture, 09:19 to 10:01. The listing page polling, then a manual registration that got "event is full". **Not stripped**: it also holds unrelated work traffic, and the RSVP body holds a real name and work email. Treat as personal data. |
| `vow_2026-10-01_09-54-12.log` (repo root, not committed) | `scripts/VowTickets.cs` run that polled the events list every ~340 ms from 10:00:00.524 to 10:15:01 (2,619 requests). That version waited for a `journey_id` field the list never sends (11.2), so it never went on to the RSVP. Fixed since. |

### 11.1 Timeline

| Time | Source | Event |
|---|---|---|
| 09:19:33 to 09:59:51 | browser | Listing polls (every 20 s, at times every 60 s): both shows `coming_soon`. Last one at 09:59:51.75. |
| 10:00:00.524 | script | **First poll: both shows already `open`**, with `register_url`. So registration opened between 09:59:52 and 10:00:00.5, probably at 10:00:00. |
| 10:00:12 | browser | First browser poll after 10:00: `open`. |
| 10:00:34.8 | browser | `GET load-for-visitor` sent. The response (Date header 14:00:41 UTC) took about **7 s** and already had `signup_count` 305 of `capacity` 305, `is_over_capacity: true`. The Dress Rehearsal was **full within about 41 s** of opening. |
| 10:01:00.1 | browser | `PUT rsvp` returned 422 `{"error":"This event is full.","capacity_full":true,"capacity_step_id":6176}`, `x-ratelimit-remaining: 9`. |
| about 10:02:29.5 to 10:02:29.9 | script | **Both shows changed to `closed` in the same poll interval**, and `register_url` disappeared. |
| to 10:15:01 | script | Still `closed`. |

### 11.2 The events list by state

| State | Fields present |
|---|---|
| `coming_soon` (before 10:00) | `uuid`, `name`, `starts_at`, `theme`, `status` |
| `open` | the same plus `register_url` (`https://go.vow.app/event/{uuid}/journeys/{journey_id}`) |
| `closed` (after about 10:02:30) | the same as `coming_soon` |

This differs from the Sep 24 response in section 4, which had `journey_id`, `capacity`, `attending_count`, `opens_at`, `closes_at`,
`timezone` and `location`. It is not known whether the API changed between the two weeks or the fields depend on something else.
A script must take the journey id from the end of `register_url`, and cannot read seat counts from the list.

Event list behaviour under load (script log): all 2,619 requests returned 200; median 87 ms, slowest 628 ms (the first request,
connection setup) and a few of 400 to 490 ms around 10:00:14. No `x-ratelimit-*` headers and no throttling at about 3 requests per
second. The list stayed fast while `load-for-visitor` took about 7 s, so it is cheap to poll and polling every 250 ms is safe.

`closed` arrived for both shows at the same moment, nearly two minutes after the Dress Rehearsal was full, so it looks like a scheduled
or manual switch, not an immediate reaction to capacity. **An `open` status does not mean seats are left**; the useful window was
under a minute.

### 11.3 IDs for this week

| Item | Dress Rehearsal | Live Show |
|---|---|---|
| Event uuid | `a082cd50-730a-4ad9-a1d8-c2d1ded4d3fe` | `23b0b0da-4cfe-4054-b211-b22cc2f0e020` |
| Journey id | 1384 | 1382 |
| Steps (landing, RSVP, confirmation, closed) | 6173, 6174, 6175, 6176 (`capacity_step_id` 6176) | not captured |
| `max_plus_ones` | 1 (same as Sep 24) | not captured |
| Capacity | 305 | not captured |
| Journey `created_at` | 2026-09-24 18:56:23 UTC (the afternoon of the previous opening) | not captured |

### 11.4 Other observations

- **The browser sent no Laravel cookies to the API.** Its `load-for-visitor` and `PUT rsvp` carried only the `AWSALBCORS` load-balancer cookie
  (no `vow_session`, `XSRF-TOKEN` or the random-named cookie), against a show that was open when the page loaded. With the Sep 24 no-cookie
  test (7.6), this is good evidence the RSVP does not need them.
- **The RSVP request matched section 5.4**: same headers, `X-Socket-ID` set, body
  `{"rsvp":true,"plus_ones":0,"me":null,"journey":1384,"group_id":null,"first_name":...,"last_name":...,"email":...}`.
- **The rate-limit counter was back to full a week later**: `x-ratelimit-remaining: 9` after the one RSVP, so the five test requests of Sep 24 had expired.
- **The registration front end was redeployed.** `go.vow.app` now serves a Nuxt 3-style shell (`data-nuxt-data`, `buildId`), not the Nuxt 2
  markup in section 2. Its config has `sanctum.mode: "token"` (token auth for logged-in users, not session cookies). The API calls an
  anonymous visitor makes were unchanged.
- **The events list now sets the Laravel cookies** (`XSRF-TOKEN`, `vow_session`, a random-named one) on a script's first request.
  Section 4 described it as cookie-free.
- **The success response of `PUT rsvp` is still not captured** (open item 7.5.1).

### 11.5 Consequences for `scripts/VowTickets.cs`

- Poll the events list until the chosen show is `open`, take the journey id from `register_url`, and go straight on. Implemented
  (`Step1b_PollUntilOpen`, `Step1_GetEventsList`).
- Stop if the show is `closed`: it does not reopen. Implemented.
- Start polling a few seconds before 10:00. The show was already open at 10:00:00.5, so the exact opening time (and any early opening or
  local clock error) was not observed. Not implemented yet: the script starts at 10:00:00.5.
- Spend as little time as possible between seeing `open` and sending the RSVP: the show was full in about 41 s, and `load-for-visitor` alone
  took about 7 s. This is why `--mode 1` (RSVP only) exists.
