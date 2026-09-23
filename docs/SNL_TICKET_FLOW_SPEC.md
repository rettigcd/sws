# SNL Ticket Flow Spec

What a browser sent to get Saturday Night Live standby tickets through the Qudini booking widget, which earlier response supplied each value, and what is still unexplained. It is built from `saz/snl_may_14/snl_may_14.exchanges.json` (173 exchanges, captured 2026-05-14 about 10:00-10:02 local) and is meant to be enough to write a C# session class like `SnlTicketSession`.

Terms: an **exchange** is one request/response pair, and a **session** is a series of exchanges. Exchange numbers below are the `ExchangeId` in the JSON. "Index" is the zero-based position in the file.

## 1. Findings in brief

- The capture holds **four page loads** and **four booking submissions**. None succeeded: three got `400 Event is full or group size exceeds available slots` and the newest got a `504` from Cloudflare.
- Every input of the booking request is accounted for. **There are no Unknown inputs.** Inputs come from an earlier response, from a constant, from user data, or from the browser.
- The booking body has no `mobileNumber`, and that is correct for this series. The series config returned by exchange 137 has `phoneNumberState: "HIDDEN"` and `phoneNumberMandatory: false`. The icecream series is `"OPTIONAL"`, which is why icecream1 could send a number. So the missing phone number does not explain the 400.
- The event list said `slotsAvailable: 300` at 10:00:24 (exchange 34), 13 seconds *before* the first 400 at 10:00:37, and still said 300 at 10:01:06 (exchanges 140 and 141), the same second as the third 400. At 10:02:08 (exchanges 191 and 193) it said `0`. Most likely the event sold out during the rush and the list lagged behind the booking endpoint. That is a reading of the timestamps, not something the capture proves.
- Qudini's backend was struggling. 13 exchanges returned `504`: two GETs (8, 89) and 11 POSTs (28, 60, 61, 65, 88, 110, 114, 115, 142, 159 and the final submission, 169). The POSTs include the register-session call in load 1 (28) and the create-event-booking-session call (159) in the load that produced the final submission.
- icecream2 (a clean fresh start) shows **no early variables that SNL is missing**, and suggests step 3 (register session) is unnecessary. See section 7.
- The `PLAY_SESSION` cookie is **re-issued by almost every response** (each one sets a new value), so a chain of "who supplied this cookie" runs through every exchange before the submission. An HTTP client with a cookie jar handles this without any code.

## 2. The page loads and the submissions

| Load | Index GET | `Q-BW-SESSION-ID` set | Submission | Time | groupSize | Result |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 11 | `139f2d29-...` | 64 | 10:00:37 | 1 | 400 Event is full or group size exceeds available slots |
| 2 | 67 | `1ea6bb76-...` | 116 | 10:01:01 | 2 | 400 (same) |
| 2 | 67 | `1ea6bb76-...` | 119 | 10:01:06 | 2 | 400 (same) |
| 3 | 118 | `2a380306-...` | **169 (latest)** | 10:01:53 | 2 | 504 Cloudflare gateway time-out |
| 4 | 171 | (new) | none | - | - | - |

(Every load had some `504`s on the `/session` POSTs. Exchanges 6 and 8, at the very start, are two failed attempts (500, 504) at the index page.)

The event list (exchange 34, 87, 140, 141, 191, 193) has two events: `IGRHK87ZUJ6` / id **52549** "DRESS REHEARSAL STANDBY" and `IZ0PPJ1L6CV` / id 52550 "LIVE SHOW STANDBY", both `maxGroupSize: 2`, `maxReservations: 300`. All four submissions were for 52549.

## 3. The booking request (exchange 169)

`POST https://bookings-us.qudini.com/booking-widget/series/{seriesId}/event/book`, and 64, 116 and 119 have the same shape.

| Part | Name | Value | Source |
| --- | --- | --- | --- |
| path | seriesId | `B9KIOO7ZIQF` | pre-known constant (it is in the page URL the user opens) |
| path | `booking-widget`, `series`, `event`, `book` | literal | pre-known constant (route) |
| body | firstName, lastName, email | user data | pre-known constant (user-supplied) |
| body | groupSize | `2` | pre-known constant (user-supplied; the event's `maxGroupSize` is 2) |
| body | eventId | `52549` | **exchange 34** `$[0].id` (also in 87, 140, 141) |
| body | timezone | `America/New_York` | pre-known constant (assumed; the trace found it in exchange 31 `$[0].timeZone`, the venue's time zone, but it is fixed for this venue) |
| body | attribution | `No answer` | **exchange 29** `$.attributionQuestions[0]` (also 84, 137) |
| body | language | `en` | pre-known constant |
| cookie | `Q-BW-USER-ID` | uuid | **exchange 11** Set-Cookie |
| cookie | `bookerIdentifier` | uuid | **exchange 11** Set-Cookie (unchanged for the whole capture) |
| cookie | `Q-BW-SESSION-ID` | uuid | **exchange 118** Set-Cookie (a new value for each index page load) |
| cookie | `PLAY_SESSION` | JWT | **exchange 168** Set-Cookie (rolling; see section 6) |
| header | `Content-Type` | `application/json;charset=UTF-8` | pre-known constant |
| header | `Origin` | `https://bookings-us.qudini.com` | pre-known constant (browser-derived from the page) |
| header | `Referer` | `https://bookings-us.qudini.com/booking-widget/events/B9KIOO7ZIQF` | pre-known constant (browser-derived: the URL of the index page) |
| header | `Accept`, `Accept-Encoding`, `Accept-Language`, `Cache-Control`, `Connection`, `Host`, `Pragma`, `sec-ch-ua`, `sec-ch-ua-mobile`, `sec-ch-ua-platform`, `Sec-Fetch-Dest`, `Sec-Fetch-Mode`, `Sec-Fetch-Site`, `User-Agent` | Chrome 148 on Windows values | browser default |
| header | `Content-Length` | 172 | computed by the HTTP client |

Note that "first supplier" is the **earliest** response in the capture that carried the value. eventId and attribution therefore point at exchanges in load 1 (29, 34), while `Q-BW-SESSION-ID` points at load 3. The same values are re-supplied in load 3 by 137 and 140 or 141, so nothing is lost by using those instead.

## 4. What the trace says participated (latest submission, 169)

The trace follows every input back to the earliest response that carried it, then does the same for that exchange's own inputs. 16 of 173 exchanges participate. **157 do not.**

| # | Index | Request | What it contributes (used later by) |
| --- | --- | --- | --- |
| 2 | 1 | `GET /series-translation/en/{seriesId}` | `eventLabel` = `Event`, the `category` of every analytics event |
| 11 | 8 | `GET /booking-widget/events/{seriesId}` (load 1 index) | the four cookies for load 1: `Q-BW-USER-ID`, `bookerIdentifier`, `Q-BW-SESSION-ID`, `PLAY_SESSION` |
| 29 | 19 | `GET /booking-widget/event/series/{seriesId}` | `attributionQuestions[0]` -> body `attribution` |
| 31 | 21 | `GET /booking-widget/event/venues/{seriesId}` | `[0].timeZone` -> body `timezone` (now treated as a constant, see below) |
| 33 | 23 | `POST /event-series/{seriesId}/session/{sessionId}/events` (analytics) | new `PLAY_SESSION`; access-control-allow-origin |
| 34 | 24 | `GET /booking-widget/event/events/{seriesId}` | `[0].id` -> body `eventId` |
| 55 | 43 | analytics POST | new `PLAY_SESSION` |
| 67 | 55 | `GET /booking-widget/events/{seriesId}` (load 2 index) | new `Q-BW-SESSION-ID` (load 2), new `PLAY_SESSION` |
| 83 | 64 | `POST /event-series/{seriesId}/session` (register session) | new `PLAY_SESSION` |
| 111 | 90 | analytics POST | new `PLAY_SESSION` |
| 118 | 97 | `GET /booking-widget/events/{seriesId}` (load 3 index) | `Q-BW-SESSION-ID` `2a380306-...` used by 169; new `PLAY_SESSION` |
| 136 | 108 | `POST /event-series/{seriesId}/session` (register session) | new `PLAY_SESSION` |
| 161 | 131 | analytics POST | new `PLAY_SESSION` |
| 167 | 137 | analytics POST | new `PLAY_SESSION` |
| 168 | 138 | analytics POST | new `PLAY_SESSION` used by 169 |
| 169 | 139 | `POST /booking-widget/series/{seriesId}/event/book` | (the submission) |

Note: the trace was generated before `timezone` was treated as pre-known. With that assumption exchange 31 no longer participates, which leaves 15 participants.

Two things worth knowing about this list:

- **It spans three page loads**, because the `PLAY_SESSION` cookie was passed from load to load (the browser kept it) and because stable values (eventId, attribution) resolve to their first appearance in load 1.
- **Most of the analytics POSTs are only here through `PLAY_SESSION`.** They are not required for the data in the booking body. Whether the server *requires* the sequence is not something the trace can say.

The 64, 116 and 119 traces are similar (8, 11 and 12 participants) and are listed in `saz/snl_may_14/snl_may_14.trace.md`.

## 5. Suggested single-load sequence

One page load, taken from load 3 (exchanges 118 to 169), with the source of each input. The generated trace only covers exchanges that feed the booking request. The steps marked *(no data used)* are included because a browser sends them and `SnlTicketSession` mirrors them.

Let `S` = `B9KIOO7ZIQF`. Cookies `Q-BW-USER-ID`, `bookerIdentifier`, `Q-BW-SESSION-ID`, `PLAY_SESSION` are sent by the cookie jar on every request after step 1.

Needed: ✓ = a later request uses something from this step (or, for step 9, it is worth keeping); X = not needed for the booking data. Steps 3 and 9 are judgement calls, see the notes below the table.

| Step | Needed | Exchange | Request | Inputs and where they come from | Outputs used later |
| --- | --- | --- | --- | --- | --- |
| 1 | ✓ | 118 | `GET /booking-widget/events/S` | constant `S`; no cookies on a fresh start | Set-Cookie: `Q-BW-USER-ID`, `bookerIdentifier`, `Q-BW-SESSION-ID`, `PLAY_SESSION` |
| 2 | X | 125-135 | scripts (`eventsBooking.min.js`, `sentry_bundle.js`, analytics) and 5 HTML templates | constants | *(no data used)* |
| 3 | X | 136 | `POST /event-series/S/session` | body `userID` = cookie `Q-BW-USER-ID` (exchange 118); body `sessions[0].sessionID` = cookie `Q-BW-SESSION-ID` (exchange 118); `device` `unknown`, `os` `windows`, `osVersion` `windows-10`, `browser` `chrome`, `browserVersion` `148.0.0.0` constants; `referrer` = the index page URL; `path`, `action`, `kioskIdentifier` null, `properties` `[]` | response `{"status":200,"message":"Session saved."}`; new `PLAY_SESSION` |
| 4 | ✓ | 137 | `GET /booking-widget/event/series/S` | constant | `attributionQuestions[0]` (`No answer`); `phoneNumberState` (`HIDDEN`), `firstNameMandatory`, `lastNameMandatory`, `emailAddressMandatory` |
| 5 | X | 138 | `GET /booking-widget/event/topics/S` | constant | *(no data used)* |
| 6 | X | 139 | `GET /booking-widget/event/venues/S` | constant | `[0].timeZone` (`America/New_York`), which is treated as a pre-known constant, so nothing is used |
| 7 | ✓ | 140 / 141 | `GET /booking-widget/event/events/S` | constant | `[i].id` -> body `eventId`; `[i].identifier` -> path of step 9; `slotsAvailable`, `maxGroupSize`, title |
| 8 | X | 142 | `POST /event-series/S/session/{Q-BW-SESSION-ID}/events` | path = cookie `Q-BW-SESSION-ID`; body: analytics events "Select Date", "Select Topics", "Select Store" with `category` = `Event` (from exchange 2) | 504 here; retried in 160 |
| 9 | ✓ | 159 | `POST /event-series/S/events/{identifier}/session` | path `identifier` from step 7; body same shape as step 3 (`userID`, `sessionID`, device fields), no `sessions` array | 504 here (capture); icecream1 got 200 `Event Related Session saved.` |
| 10 | X | 160, 161 | analytics POSTs: Select Date/Topics/Store, "Select Item Event Thumbnail" (label uses the event title), "Select Event Thumbnail" | path = cookie `Q-BW-SESSION-ID` | *(no data used)* |
| 11 | X | 165, 166 | templates: group-size, customer-details | constants | *(no data used)* |
| 12 | X | 167, 168 | analytics POSTs: "Book Event Button Event Details", field clicks (`firstName`, ...) | path = cookie `Q-BW-SESSION-ID` | `PLAY_SESSION` |
| 13 | ✓ | 169 | `POST /booking-widget/series/S/event/book` | section 3 | (504) |
| 14 | X | 170 | analytics POST "Complete Button Customer Details" | path = cookie `Q-BW-SESSION-ID` | response was `404 Session not found` |

### What each step does, in plain terms

"Needed" means the booking request uses something from that step. Where the capture cannot prove a step is required, this says so.

1. **Open the booking page.** This is the visit to the ticket page. The server hands back four cookies (user ID, booker ID, session ID and a rolling `PLAY_SESSION`) that identify you for every later request. Needed: without these cookies the booking has no identity.
2. **Load the page's scripts and templates.** A browser downloads the page's JavaScript and HTML pieces (footer, popups). None of their content goes into the booking, so this is browser noise. A script doing the booking directly can probably skip these, but the capture cannot prove the server doesn't check.
3. **Register the widget session.** This tells Qudini "this user ID and session ID just opened the widget", along with device details (Windows, Chrome). The user ID and session ID come from the cookies in step 1. It also refreshes `PLAY_SESSION`. Probably not needed: neither icecream capture makes this call, and icecream1 still booked successfully (see section 7). The session ID it announces is the cookie from step 1, which the later URLs use with or without this call.
4. **Get the series settings.** This returns how the booking form is configured, including which fields are required (name and email yes, phone hidden) and the list of "how did you hear about us" answers. The first answer, `No answer`, goes into the booking as `attribution`. Needed for that value.
5. **Get the topics list.** This is for the topic filter in the page. Nothing from it goes into the booking. Not needed, but a browser does it anyway.
6. **Get the venue.** This returns where the event is, including its time zone, `America/New_York`. The booking sends that same value as `timezone`, but because it is treated as pre-known (fixed for this venue), nothing here is needed. Not needed.
7. **Get the events list.** This returns the available shows (Dress Rehearsal and Live Show standby) with their numeric ID, short code, seats left and maximum group size. The numeric ID (`52549`) becomes `eventId` in the booking, and the short code is used in step 9. Needed.
8. **Send "user picked a date/topic/store" analytics.** This reports which filters you clicked. It is usage tracking, not part of the booking. It failed with a 504 here and was resent in step 10. Not needed, apart from refreshing `PLAY_SESSION`.
9. **Create the event booking session.** This tells Qudini which specific show you are looking at, with the same identity details as step 3. It plausibly sets up state on the server, but the trace cannot say for sure. In the capture it worked in the first two loads and returned a 504 in this one, and the booking failed either way. Probably worth doing, since icecream1 did it and its booking succeeded.
10. **More analytics.** These report clicks on the date, topics, store and event thumbnail. Tracking only. Not needed.
11. **Load the group-size and customer-details templates.** The browser downloads the forms shown next. No data flows into the booking. Not needed.
12. **Report clicks on the booking form.** These report "Book Event" and field clicks. Tracking only, but the last of these returns the latest `PLAY_SESSION` cookie that the booking uses. A cookie jar handles that automatically.
13. **Submit the booking.** This is the actual request for tickets. It sends first name, last name, email, group size, the event ID from step 7, the time zone from step 6, the attribution from step 4 and `en` for language, plus all four cookies. Needed: this is the goal.
14. **Report "Complete".** This is a final tracking event after submitting. In the capture it returned `404 Session not found`. Not needed.

Steps 3 and 9 are the only ones whose necessity is uncertain. Step 9 is worth keeping, because the successful booking in icecream1 did it. Step 3 is probably unnecessary, because icecream1 booked successfully without it; keeping it is harmless but adds a request and a chance of a 504.

## 6. Cookies

| Cookie | Set by | Behaviour |
| --- | --- | --- |
| `Q-BW-USER-ID` | index page GET | Same for the whole capture (it stays with the browser). |
| `bookerIdentifier` | index page GET | Same for the whole capture. |
| `Q-BW-SESSION-ID` | index page GET | **New value for every index page load.** It is also the `sessionID` in `POST /session` bodies and the `{sessionId}` in `.../session/{sessionId}/events` URLs. |
| `PLAY_SESSION` | almost every qudini response | A signed JWT that is replaced by (nearly) every response, and carries `bookerIdentifier`. Send back the latest one; a cookie jar does this on its own. |

## 7. Differences from icecream1 and icecream2

- **icecream1** (`saz/icecream1`, 20 exchanges) is the only capture with a *successful* booking (exchange 34, HTTP 200, reference `TI9KMM13KRG`). Its request has the same shape as the SNL one, plus `mobileNumber` (`+15135551212`). The icecream series config has `phoneNumberState: "OPTIONAL"`; SNL's is `"HIDDEN"`.
- **icecream1 does the event-booking-session call and it works** (exchange 12: `POST /event-series/{S}/events/{identifier}/session` -> 200 `Event Related Session saved.`); in the SNL capture the same call returned 200 in loads 1 and 2 (54, 109) and 504 in load 3 (159).
- **icecream2** (`saz/icecream2`, 29 exchanges) is the cleanest fresh start but stops at `group-size.html`, before any booking POST, so it cannot show the final step. It opens on `GET /booking-widget/events/{S}/event/choose`; the SNL capture opens on `GET /booking-widget/events/{S}` (no `/event/choose`).
- **No early variables are missing.** icecream2's first request has no cookies and its response sets all four cookies, like SNL's index response. Its index HTML holds no embedded IDs or tokens (no `eventId`, 3 script tags, as in SNL). Its request bodies confirm the sources traced for SNL: in exchange 42 the body `userID` equals the `Q-BW-USER-ID` cookie and `sessionID` equals the `Q-BW-SESSION-ID` cookie.
- **Neither icecream capture makes the register-session call** (`POST /event-series/{S}/session`, step 3), and icecream1 booked successfully without it. They send only the analytics events and the event-booking-session call. SNL made the register call in every load and still failed, so step 3 looks like an unneeded extra.
- **The `/event/choose` entry URL causes an extra request.** Opening icecream2's URL makes the page request `/booking-widget/event/eventId/choose?timezone=...` with the literal word `eventId` (exchange 22, HTTP 500; exchange 5 in icecream1). The SNL entry URL has no `/event/choose`, and the SNL capture has no such request. `IceCreamTicketSession` replays it as a throw-away step.
- **Fresh navigation vs reload.** icecream2's first request has `Sec-Fetch-Site: none` and no `Referer` (a typed URL); SNL's index request in load 3 has the page itself as `Referer` (a reload). Not a missing variable.
- **Header differences in the JSON are not real.** Headers shared by every request are moved into the `GlobalHeaders` block of `.exchanges.json`, so icecream2's first request looks like it lacks User-Agent, Accept-Encoding and similar.
- Both icecream captures use series `UZJLSRJUNZC` and a different event identifier (`AQTAPFO2R6C`, id 54920), so those values are not shared.
- In icecream1 the booking request's `timezone` is a constant (no earlier response carried `America/New_York`), while in SNL the venues response carries `timeZone`. Both are now treated as a pre-known constant.

## 8. Likely reasons the SNL session failed (unproven)

1. **The event was full.** The 400 text says so. The slot count reads 300 at 10:00:24 and 10:01:06 but 0 by 10:02:08, so the list may have lagged, or the 300 seats went quickly during the release. groupSize 1 (exchange 64) failed the same way as groupSize 2, so group size is unlikely.
2. **Backend overload.** Many 504s on the `/session` endpoints (11 POSTs in all), including 159 (event booking session) and 169 itself. The 504 body says `retryable: true` and `retry_after: 120`, so a client should back off rather than retry immediately.
3. **Missing session registration for the booking.** In loads 1 and 2 the event-booking-session call (54, 109) succeeded and the booking still returned 400, so this is not the sole cause.
4. **An unneeded extra step.** SNL registers the widget session (step 3) but neither icecream flow does, and icecream1's booking succeeded. This is unlikely to cause a 400, but it adds a request that can 504.
5. **Not a missing input.** Every value in the booking body is explained above and none is Unknown. `mobileNumber` is not needed for this series.

## Appendix A: exchanges that did not participate (latest submission)

157 exchanges. `{id}` stands for an ID-like path segment.

| Request pattern | Count | Exchange ids |
| --- | --- | --- |
| GET code.angularjs.org/1.5.11/i18n/angular-locale_en.js | 5 | 1, 50, 106, 163, 210 |
| GET oneocsp.microsoft.com/ocsp/{id} | 3 | 4, 62, 66 |
| GET main.vscode-cdn.net/extensions/marketplace.json | 1 | 5 |
| GET bookings-us.qudini.com/booking-widget/events/B9KIOO7ZIQF | 3 | 6, 8, 171 |
| GET api.jobright.ai/swan/auth/user-stage | 1 | 7 |
| GET ipv6.msftncsi.com/connecttest.txt | 1 | 9 |
| GET bookings-us.qudini.com/public/javascripts/eventsBooking.min.js | 4 | 16, 72, 125, 176 |
| GET bookings-us.qudini.com/public/sentry_bundle.js | 4 | 17, 73, 126, 177 |
| GET www.google-analytics.com/analytics.js | 4 | 18, 74, 127, 178 |
| GET o.pki.goog/we2/{id} | 1 | 21 |
| GET bookings-us.qudini.com/view/bookingEventWidget.html | 4 | 23, 78, 131, 182 |
| GET bookings-us.qudini.com/shared/footer/q-footer.html | 4 | 24, 79, 132, 183 |
| GET bookings-us.qudini.com/eventsBooking/components/popup/popup-appointment-slot-expired.html | 4 | 25, 80, 133, 184 |
| GET bookings-us.qudini.com/eventsBooking/components/popup/popup-event-has-passed.html | 4 | 26, 81, 134, 185 |
| GET bookings-us.qudini.com/eventsBooking/components/popup/popup-membership-message.html | 4 | 27, 82, 135, 186 |
| POST bookings-us.qudini.com/event-series/B9KIOO7ZIQF/session | 2 | 28, 187 |
| GET bookings-us.qudini.com/booking-widget/event/topics/B9KIOO7ZIQF | 4 | 30, 85, 138, 189 |
| GET bookings-us.qudini.com/booking-widget/event/events/B9KIOO7ZIQF | 7 | 32, 87, 89, 140, 141, 191, 193 |
| GET bookings-us.qudini.com/eventsBooking/components/choose-event/choose-event.html | 4 | 35, 90, 143, 194 |
| GET bookings-us.qudini.com/eventsBooking/components/select-language/select-language.html | 4 | 36, 91, 144, 195 |
| GET bookings-us.qudini.com/shared/cookie-policy/cookie-policy.html | 4 | 37, 92, 145, 196 |
| GET bookings-us.qudini.com/shared/privacy-policy/privacy-policy.html | 4 | 38, 93, 148, 197 |
| GET bookings-us.qudini.com/shared/terms-conditions/terms-conditions.html | 4 | 39, 94, 149, 198 |
| GET us.qudini.com/api/v3/merchants/{id}/privacy-policy | 4 | 40, 95, 150, 199 |
| GET bookings-us.qudini.com/eventsBooking/components/datepicker/datepicker.html | 4 | 41, 97, 152, 200 |
| GET us.qudini.com/api/v3/merchants/{id}/cookie-policy | 4 | 42, 96, 147, 207 |
| GET us.qudini.com/api/v3/merchants/{id}/terms-conditions | 4 | 44, 98, 151, 208 |
| GET bookings-us.qudini.com/eventsBooking/components/filter-topics/filter-topics.html | 4 | 45, 100, 153, 202 |
| GET bookings-us.qudini.com/eventsBooking/components/other-stores/stores.html | 4 | 46, 101, 154, 203 |
| GET bookings-us.qudini.com/eventsBooking/components/social-share-buttons/social-share-buttons.html | 4 | 47, 102, 155, 204 |
| GET bookings-us.qudini.com/eventsBooking/components/choose-event/event-thumbnail.html | 4 | 48, 103, 156, 205 |
| GET bookings-us.qudini.com/series-languages/B9KIOO7ZIQF | 4 | 49, 105, 162, 206 |
| GET bookings-us.qudini.com/series-translation/en/B9KIOO7ZIQF | 4 | 51, 107, 164, 211 |
| GET bookings-us.qudini.com/eventsBooking/components/event-details/event-details.html | 3 | 53, 108, 158 |
| POST bookings-us.qudini.com/event-series/B9KIOO7ZIQF/events/IGRHK87ZUJ6/session | 3 | 54, 109, 159 |
| POST bookings-us.qudini.com/event-series/B9KIOO7ZIQF/session/{id}/events | 14 | 56, 60, 61, 65, 88, 110, 114, 115, 117, 120, 142, 160, 170, 192 |
| GET bookings-us.qudini.com/eventsBooking/components/group-size/group-size.html | 3 | 57, 112, 165 |
| GET o.pki.goog/wr2/{id} | 1 | 58 |
| GET bookings-us.qudini.com/eventsBooking/components/customer-details/customer-details.html | 3 | 59, 113, 166 |
| GET vscode-sync.trafficmanager.net/v1/manifest | 1 | 63 |
| POST bookings-us.qudini.com/booking-widget/series/B9KIOO7ZIQF/event/book | 3 | 64, 116, 119 |
| GET bookings-us.qudini.com/booking-widget/event/series/B9KIOO7ZIQF | 3 | 84, 137, 188 |
| GET bookings-us.qudini.com/booking-widget/event/venues/B9KIOO7ZIQF | 3 | 86, 139, 190 |

Exchanges 4, 5, 7, 9, 21, 58, 62, 63, 66 (Microsoft/Google/VS Code/jobright traffic) are unrelated to the site.

## Appendix B: regenerating the trace

```
dotnet run --project app -- saz/snl_may_14/snl_may_14.saz --trace
```

writes `snl_may_14.trace.md` (readable) and `snl_may_14.trace.json` (full detail, one trace per submission, every exchange listed with its inputs, sources and outputs) next to the `.saz`. Labels: **Exchange** (an earlier response supplied it), **PreKnownConstant**, **BrowserDefault**, **Unknown**.
