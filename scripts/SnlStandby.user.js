// ==UserScript==
// @name         SNL Standby (vow.app)
// @namespace    https://github.com/rettigcd/sws
// @version      1.0
// @description  Picks the Live Show or the Dress Rehearsal on the NBC SNL Standby page, fills in the vow.app registration form, sets the group size and submits.
// @match        https://snlstandby.nbcuni.com/*
// @match        https://pro.vow.app/public/nbc*
// @match        https://go.vow.app/event/*/journeys/*
// @match        http://localhost:*/*
// @run-at       document-start
// @icon         https://www.google.com/s2/favicons?sz=64&domain=nbc.com
// @grant        none
// ==/UserScript==

// How it works (details in docs/VOW_SNL_FLOW.md):
//   https://snlstandby.nbcuni.com/  is a static page with an iframe to  https://pro.vow.app/public/nbc  (the show list).
//   The list's "Register Now" link goes to  https://go.vow.app/event/{uuid}/journeys/{id}  (the registration page, a single-page app).
//   Tampermonkey runs this script in EVERY frame whose URL matches one of the @match lines, so it does not matter whether you open the NBC page
//   or the iframe's own URL. The copy in the NBC page itself does nothing (it cannot see into a cross-origin iframe); the copy inside the iframe
//   does the work, and runs again after the iframe navigates to the registration page.
//
//   Stage 1 (pro.vow.app, the list):        wait until the chosen show is "open", then go to its registration page.
//   Stage 2 (go.vow.app, registration):     click "BOOK STANDBY RESERVATION", fill in first name / last name / email, set the group size, click SUBMIT.
//
// Nothing is sent by this script itself. It only fills in the page and clicks its buttons, so the site makes the same requests as when you do it by hand.
// The one exception is the list check in stage 1 (FAST_POLL_MS), a plain GET of the same public list the page loads.
// It also keeps a log of every request and response the pages make (time stamps, headers, whole bodies) and saves it as a file in your downloads when
// the run succeeds or 2 minutes after the open time (see REQUEST LOG below and CONFIG.LOG_ENABLED).
//
// The RSVP response is recorded (console, and localStorage key "snlRsvpLog") because a SUCCESSFUL response has never been captured.

(function () {
	'use strict';

	// =====================================================================
	// ==========================  CONFIGURATION  ==========================
	// =====================================================================
	const CONFIG = {
		// The six settings below belong to a PROFILE. The profiles you make in the settings panel on the page (top right, "SNL profile: ...")
		// are kept in localStorage and replace these values when the script starts. These values are only the built-in TEST profile, used when
		// no profile is saved yet (and with the local replay server).
		SHOW: 'dress',                      // 'dress' = Dress Rehearsal, 'live' = Live Show

		FIRST_NAME: 'Test',                 // On the real sites the script refuses to run while the email is still test@example.com (or any value is CHANGE_ME).
		LAST_NAME: 'Person',
		EMAIL: 'test@example.com',
		GROUP_SIZE: 2,                      // total people, including you. The site allows 1 or 2.

		SUBMIT: true,                       // false = fill in the form but do NOT click SUBMIT (for practice)

		PROFILE_WAIT_MS: 1500,              // the copy of the script inside the iframe asks the page around it for the active profile; how long it waits for the answer

		// When the show opens: the next OPEN_WEEKDAY (0 = Sunday ... 4 = Thursday ... 6 = Saturday) at OPEN_HOUR:OPEN_MINUTE, in this computer's time zone.
		// Until RAPID_POLL_LEAD_SECONDS before that moment the script only shows a countdown and does NO polling of its own and none of the SPEEDUP work
		// (the page's own 20 s refresh still runs). Started on the open day after the open time, it is treated as already open if that was less than
		// GIVE_UP_AFTER_MIN ago, otherwise it waits for next week.
		OPEN_WEEKDAY: 4,
		OPEN_HOUR: 10,
		OPEN_MINUTE: 0,
		RAPID_POLL_LEAD_SECONDS: 5,

		// The rapid polling, once it has started: the script fetches the show list itself and starts a new request every FAST_POLL_MS (1000 = one a
		// second) WITHOUT waiting for the earlier ones, so a slow or hung request cannot hold up the next. A request is cancelled after POLL_TIMEOUT_MS,
		// at most MAX_POLLS_IN_FLIGHT are out at once (so we do not use up the browser's connections), and the first answer that shows the show open
		// wins: the other requests are cancelled. The page's own 20 s refresh is left alone.
		FAST_POLL_MS: 1000,
		POLL_TIMEOUT_MS: 5000,
		MAX_POLLS_IN_FLIGHT: 5,
		GIVE_UP_AFTER_MIN: 90,              // stage 1: stop waiting this many minutes after the open time
		STEP_TIMEOUT_MS: 20000,             // stage 2: how long to wait for each page/step to appear. Starts when the registration page starts (after the
		                                    // show opened), NOT during the wait for the open. The page's own startup (auth check, then the journey load)
		                                    // took about 16 s at the 2026-10-01 go-live, so much less than this gives up while it is still loading.

		// stage 2: if the registration page cannot load its journey (the call fails with an HTTP error or a network error, the page says "link is no
		// longer available", or nothing appears within STEP_TIMEOUT_MS), reload it. RELOAD_ATTEMPTS counts ALL tries, including the first load:
		// 4 = the first load and up to 3 reloads. RELOAD_WAIT_MS is the wait between a failure and the reload. The count is kept in sessionStorage
		// (this tab only, forgotten after 5 minutes or once the page loads). A failed RSVP is never retried by a reload.
		RELOAD_ATTEMPTS: 4,
		RELOAD_WAIT_MS: 1000,

		// stage 1 -> 2: once the list says the show is open, the script navigates to the registration page. If that page has not started to arrive after
		// NAVIGATE_TIMEOUT_MS (the server does not answer), the navigation is started again, NAVIGATE_ATTEMPTS tries in all. This is only about the page's
		// first byte: after it has started to arrive, the registration page has its own reloads (RELOAD_ATTEMPTS above).
		NAVIGATE_TIMEOUT_MS: 5000,
		NAVIGATE_ATTEMPTS: 4,

		// stage 2: if one of the registration page's own script files (<script src> in its HTML, same web address) has still not finished loading this
		// long after the page started, the page can not start (a file hangs): reload at once instead of waiting STEP_TIMEOUT_MS. The page's own API
		// calls are NOT part of this (they are slow at the go-live and the page just waits for them). The real go-live loaded its files in under 0.6 s.
		SCRIPT_LOAD_TIMEOUT_MS: 5000,

		// stage 2: after SUBMIT is clicked, if the RSVP has not been answered after this long, the banner starts to say so (and keeps counting). The script
		// never cancels or resends an RSVP; the message only tells you to wait and not to click again.
		RSVP_SLOW_NOTICE_MS: 10000,

		// ---- REQUEST LOG ----
		// Every request the pages make (fetch and XMLHttpRequest: time stamps, headers, whole request and response bodies) and their other downloads (timings
		// only) are collected by the top page and saved as a file in your downloads (snl_<date>_<time>.log): after the RSVP is accepted (once the calls that follow it are answered, at most LOG_SETTLE_MS),
		// or LOG_AFTER_OPEN_MS after the open time, whichever comes first. The RSVP body holds your name and email. false = no capture, no file.
		// The log file is a download: if Chrome is set to ask where to save each file, it asks then.
		LOG_ENABLED: true,
		LOG_AFTER_OPEN_MS: 120000,
		LOG_SETTLE_MS: 25000,   // after the RSVP is accepted: the log waits until every request has been answered (and 1.5 s have passed without news), at most this long

		DEBUG: true,                        // log to the browser console

		// ---- SPEED-UP of the list on screen (stage 1) ----
		// The list page only redraws itself every 20 s (its own timer, which this script leaves alone), so its "Register Now" button can be up to 20 s
		// late. This picks what, if anything, makes it show sooner (details in the comment above listStage).
		//   'auto' = the script looks at the page's list component a few seconds after the list page loads (and again at the start of the
		//            rapid polling) and uses the best mode the page allows: 1 if it has $set(), else 0. The countdown banner shows the choice. (default)
		// or force one mode by number:
		//   0 = change nothing: we navigate to the registration page ourselves as soon as OUR poll sees "open"  (no speed-up)
		//   1 = when OUR poll finds the show open, push the events it got into the page and follow the page's own "Register Now" link; if the
		//       push cannot be done or no link shows within PUSH_WAIT_MS, redirect to register_url ourselves (= mode 0)
		SPEEDUP: 'auto',
		CAPABILITY_WAIT_MS: 2500,           // 'auto': how long after the list page starts to wait for the page's list component before concluding it is not there
		PUSH_WAIT_MS: 1000,                 // mode 1: how long to wait for the page's link after pushing the events before redirecting ourselves

		// ---- TESTING (programmers only) ----
		// Set a number of seconds to make the LOCAL replay server (replay-server/ReplayServer.cs, page on localhost) put the show list back to
		// "coming_soon" and open it that many seconds from now (GET /__replay/open-in/N). Each time the list page starts, the open time is
		// reset like this, so reloading the page re-runs the test without restarting the server. 0 = open right away.
		// null = do nothing. It is used ONLY when the page is on localhost / 127.0.0.1 (isLocal): anywhere else it is ignored completely (not
		// even checked, nothing is shown or sent), so it can never touch the real site.
		TEST_OPEN_IN_SECONDS: null,
	};

	// =====================================================================
	// ======================  KNOWN CONSTANTS (site)  =====================
	// =====================================================================
	// Observed on 2026-09-24 (docs/VOW_SNL_FLOW.md). If vow.app is redeployed, the selectors below are what can break.
	const SHOWS = {
		dress: { label: 'Dress Rehearsal', re: /dress/i },
		live: { label: 'Live Show', re: /live/i },
	};
	// Running on the local replay server (replay-server/ReplayServer.cs) instead of the real sites? There every page and the API share one origin.
	const isLocal = location.hostname === 'localhost' || location.hostname === '127.0.0.1';
	// The @match line covers every localhost port; only the replay server's port is ours. Other local apps are left alone.
	if (isLocal && location.port !== '50219') return;
	// CONFIG.TEST_OPEN_IN_SECONDS, but only on localhost: everything below uses this, never the CONFIG value, so on a real site it is always null.
	const testOpenInSeconds = isLocal ? CONFIG.TEST_OPEN_IN_SECONDS : null;
	const LIST_API = isLocal ? '/api/v2/public/by-url/nbc/events' : 'https://api.vow.app/api/v2/public/by-url/nbc/events';   // what the list page polls; CORS allows origin https://pro.vow.app

	const SEL = {
		// stage 1: pro.vow.app/public/nbc (Nuxt 2 page). One <article class="nbc-card"> per show; the link exists only when the show is open.
		card: 'article.nbc-card',
		cardTitle: '.nbc-card__title',
		cardRegisterLink: 'a.nbc-card__cta--register',

		// stage 2: go.vow.app. The step HTML is injected with innerHTML; the buttons are <a class="v-button"> whose clicks the page handles.
		//   landing page button "BOOK STANDBY RESERVATION":  data-function="to"
		//   form page button "SUBMIT":                        data-function="rsvp_yes"
		landingButton: 'a.v-button[data-function="to"]',
		submitButton: 'a.v-button[data-function="rsvp_yes"]',
		// the form itself is a Vue widget mounted inside #VOW_custom_page_widget
		widget: '#VOW_custom_page_widget',
		input: '#VOW_custom_page_widget input.vow-glass-native-input',   // First Name, Last Name, Email, in that order (type text, text, email)
		inputLabel: '.vow-glass-label',                                  // <p> just before each input: "First Name*", ...
		stepperButton: '#VOW_custom_page_widget .vow-glass-stepper-pill__btn',   // two buttons: "−" then "+"
		stepperCount: '#VOW_custom_page_widget .vow-glass-stepper-pill__count',  // shows plus_ones + 1 = total guests
	};

	// =====================================================================
	// ============================  HELPERS  ==============================
	// =====================================================================
	if (window.__snlAutoRegister) return;
	window.__snlAutoRegister = true;

	const log = (...args) => { if (CONFIG.DEBUG) console.log('[SNL auto]', ...args); };
	const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

	/** Polls fn() until it returns something truthy; resolves null on timeout. */
	async function waitFor(fn, timeoutMs, everyMs = 100) {
		const start = Date.now();
		for (;;) {
			const value = fn();
			if (value) return value;
			if (Date.now() - start > timeoutMs) return null;
			await sleep(everyMs);
		}
	}

	let bannerEl = null;
	let bannerShown = '';   // the HTML now in the box, so it is only rewritten when it changes
	let keepOpenWarning = false;   // true only during the first wait (the countdown, before the rapid polling): the banner then starts with the "keep this window open" line
	/** Escapes a value for use inside HTML. EVERY value that is not one of our own constants (event names from the API, profile fields, server
	 *  error text, ...) must go through this before it is put into banner HTML. */
	const esc = (value) => String(value).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
	/** A small coloured label, for banner HTML. */
	const chip = (text, background, color = '#fff') => `<span style="background:${background};color:${color};border-radius:3px;padding:0 6px;font-weight:700">${esc(text)}</span>`;

	/**
	 * Small status box in the top-left corner of the frame. kind: info | ok | warn | error (the box colour).
	 * text = the message as plain text: it is what goes to the console, and what is shown when no html is given (escaped, so it can hold anything).
	 * quiet = update the screen only, no console line (for messages that repeat, such as the countdown; the caller logs a line when something changes).
	 * html = the same message as HTML, for colour and style (inline styles; use esc() and chip() above for anything that is not a fixed constant).
	 */
	async function banner(text, kind = 'info', quiet = false, html = null) {
		if (!quiet) log(`[${kind}] ${text}`);
		await waitFor(() => document.body, 10000, 50);
		if (!document.body) return;
		if (!bannerEl || !bannerEl.isConnected) {
			bannerEl = document.createElement('div');
			bannerEl.id = 'snl-auto-banner';
			bannerEl.style.cssText = 'position:fixed;top:0;left:0;z-index:2147483647;max-width:90%;padding:8px 12px;font:12px/1.45 sans-serif;color:#fff;border-bottom-right-radius:8px;box-shadow:0 2px 8px rgba(0,0,0,.35);pointer-events:none';
			document.body.appendChild(bannerEl);
			bannerShown = '';
		}
		const colors = { info: '#1f4fd8', ok: '#1a7f37', warn: '#b26a00', error: '#c62828' };
		bannerEl.style.background = colors[kind] || colors.info;
		const body = html !== null ? html : esc(text).replace(/\n/g, '<br>');
		// Chrome slows the timers of hidden or minimised tabs (to about 1 a minute after 5 minutes), which could make the script late at the open time.
		const keepOpen = keepOpenWarning ? '<div style="font-weight:700;margin-bottom:4px">Keep this window open and in the foreground as the open-time approaches.</div>' : '';
		const full = keepOpen + (profileSummaryHtml ? `<div style="margin-bottom:4px">${profileSummaryHtml}</div>` : '') + `<div>${body}</div>`;
		if (full !== bannerShown) { bannerEl.innerHTML = full; bannerShown = full; }
	}

	function configProblem() {
		if (!SHOWS[CONFIG.SHOW]) return `CONFIG.SHOW must be 'dress' or 'live' (got '${CONFIG.SHOW}').`;
		if (!CONFIG.FIRST_NAME.trim() || CONFIG.FIRST_NAME === 'CHANGE_ME') return 'Set CONFIG.FIRST_NAME.';
		if (!CONFIG.LAST_NAME.trim() || CONFIG.LAST_NAME === 'CHANGE_ME') return 'Set CONFIG.LAST_NAME.';
		if (!CONFIG.EMAIL.includes('@') || /CHANGE_ME/.test(CONFIG.EMAIL)) return 'Set CONFIG.EMAIL.';
		if (!isLocal && CONFIG.EMAIL.trim().toLowerCase() === 'test@example.com') return 'the profile still has the test email address (test@example.com). Select or make a profile with your own name and email (settings panel, top right) and reload the page.';
		if (![1, 2].includes(CONFIG.GROUP_SIZE)) return 'CONFIG.GROUP_SIZE must be 1 or 2.';
		if (!Number.isInteger(CONFIG.OPEN_WEEKDAY) || CONFIG.OPEN_WEEKDAY < 0 || CONFIG.OPEN_WEEKDAY > 6) return 'CONFIG.OPEN_WEEKDAY must be 0 (Sunday) to 6 (Saturday).';
		if (!(CONFIG.OPEN_HOUR >= 0 && CONFIG.OPEN_HOUR <= 23 && CONFIG.OPEN_MINUTE >= 0 && CONFIG.OPEN_MINUTE <= 59)) return 'CONFIG.OPEN_HOUR / OPEN_MINUTE must be a valid time of day.';
		if (!(CONFIG.RAPID_POLL_LEAD_SECONDS >= 0)) return 'CONFIG.RAPID_POLL_LEAD_SECONDS must be 0 or more.';
		if (!Number.isInteger(CONFIG.RELOAD_ATTEMPTS) || CONFIG.RELOAD_ATTEMPTS < 1) return 'CONFIG.RELOAD_ATTEMPTS must be a whole number, 1 or more.';
		if (!(CONFIG.RELOAD_WAIT_MS >= 0)) return 'CONFIG.RELOAD_WAIT_MS must be 0 or more.';
		if (!(CONFIG.SCRIPT_LOAD_TIMEOUT_MS >= 1000)) return 'CONFIG.SCRIPT_LOAD_TIMEOUT_MS must be at least 1000.';
		if (!(CONFIG.NAVIGATE_TIMEOUT_MS >= 1000)) return 'CONFIG.NAVIGATE_TIMEOUT_MS must be at least 1000.';
		if (!(CONFIG.RSVP_SLOW_NOTICE_MS >= 1000)) return 'CONFIG.RSVP_SLOW_NOTICE_MS must be at least 1000.';
		if (!(CONFIG.LOG_AFTER_OPEN_MS >= 1000 && CONFIG.LOG_SETTLE_MS >= 0)) return 'CONFIG.LOG_AFTER_OPEN_MS must be at least 1000 and LOG_SETTLE_MS 0 or more.';
		if (!Number.isInteger(CONFIG.NAVIGATE_ATTEMPTS) || CONFIG.NAVIGATE_ATTEMPTS < 1) return 'CONFIG.NAVIGATE_ATTEMPTS must be a whole number, 1 or more.';
		if (!['auto', 0, 1].includes(CONFIG.SPEEDUP)) return "CONFIG.SPEEDUP must be 'auto', 0 or 1.";
		if (!(CONFIG.FAST_POLL_MS >= 100)) return 'CONFIG.FAST_POLL_MS must be at least 100.';
		if (!(CONFIG.POLL_TIMEOUT_MS >= 500)) return 'CONFIG.POLL_TIMEOUT_MS must be at least 500.';
		if (!Number.isInteger(CONFIG.MAX_POLLS_IN_FLIGHT) || CONFIG.MAX_POLLS_IN_FLIGHT < 1) return 'CONFIG.MAX_POLLS_IN_FLIGHT must be a whole number, 1 or more.';
		if (!(CONFIG.PROFILE_WAIT_MS >= 0)) return 'CONFIG.PROFILE_WAIT_MS must be 0 or more.';
		if (!(CONFIG.CAPABILITY_WAIT_MS >= 0 && CONFIG.PUSH_WAIT_MS >= 0)) return 'CONFIG.CAPABILITY_WAIT_MS and PUSH_WAIT_MS must be 0 or more.';
		const t = testOpenInSeconds;   // only checked on localhost, where it is used
		if (t !== null && !(typeof t === 'number' && t >= 0 && t <= 86400)) return 'CONFIG.TEST_OPEN_IN_SECONDS must be null or a number of seconds from 0 to 86400.';
		return null;
	}

	/**
	 * Sets an <input>'s value the way a user typing would, so Vue's v-model sees it.
	 * Assigning input.value directly does not notify Vue; the native setter plus input/change events does.
	 */
	function fillInput(input, value) {
		const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
		input.focus();
		setValue.call(input, value);
		input.dispatchEvent(new Event('input', { bubbles: true }));
		input.dispatchEvent(new Event('change', { bubbles: true }));
		input.blur();
	}

	/**
	 * Clicks one of the site's <a class="v-button"> buttons.
	 * The page's click handler starts looking for the <a> at the click target's PARENT, so the click must land on an element INSIDE the <a>
	 * (its text <span>), not on the <a> itself. A click on the <a> itself is ignored by the page and the browser would open the link in a new tab.
	 */
	function clickButton(anchor) {
		const inner = anchor.querySelector('strong span') || anchor.querySelector('span') || anchor.firstElementChild;
		if (!inner) throw new Error('The button has no inner element to click.');
		inner.click();
	}

	// =====================================================================
	// ===================  REQUEST LOG (capture and write)  ===============
	// =====================================================================
	// Every fetch and XMLHttpRequest the pages make (the show list and the registration page, in the iframe) is recorded with time stamps, request and
	// response headers (the ones the browser lets a page read) and the WHOLE request and response body, plus the pages' other downloads (scripts,
	// styles, images: timings only, from Resource Timing). The log is written as a file (a download) when the run succeeds (RSVP accepted, after
	// the calls that follow it have been answered, at most CONFIG.LOG_SETTLE_MS), or CONFIG.LOG_AFTER_OPEN_MS after the open time, whichever comes first.
	//
	// Where things live: the iframe is a new page (a new web address, so new memory and storage) each time it moves from the list to the registration
	// page, so the pages in the iframe only CAPTURE and send what they captured to the TOP page (the one you look at, as with the profiles), which
	// COLLECTS it and writes the file. A page that is opened on its own (no trusted page around it) collects for itself.
	// A page cannot create a file while it is being left, so for "the user left the page" the collector keeps a copy of the log in its localStorage
	// (saved every couple of seconds); the next time the top page opens, that copy is written out as a "_recovered" file.
	const MSG_LOG_BATCH = 'snl-log-batch';   // frame -> top page: entries that are new or have changed
	const MSG_LOG_OPEN = 'snl-log-open';     // frame -> top page: when the show opens (ms)
	const MSG_LOG_DONE = 'snl-log-done';     // frame -> top page: the run succeeded: write the log after the calls that follow
	const LOG_KEY = 'snlRequestLog';         // localStorage of the collecting page: a copy of the log, for when the page is left before the file was written
	const documentId = Math.random().toString(36).slice(2, 8);   // tells the entries of one page load from those of another
	const epochNow = () => performance.timeOrigin + performance.now();   // milliseconds, on one timeline for every page

	// ---------- capture (in the frames that show the list and the registration page) ----------
	let capFrame = '';
	let capSeq = 0;
	const capDirty = new Set();   // entries that are new or changed since the last batch
	let capTimer = null;
	let capSink = () => {};       // takes a batch of entries: sends it to the top page, or hands it to this page's own collector

	function absoluteUrl(url) { try { return new URL(url, location.href).href; } catch (e) { return String(url); } }

	function bodyText(body) {
		if (body === undefined || body === null) return '';
		if (typeof body === 'string') return body;
		if (typeof URLSearchParams !== 'undefined' && body instanceof URLSearchParams) return body.toString();
		if (typeof FormData !== 'undefined' && body instanceof FormData) return '[form data: ' + [...body.keys()].join(', ') + ']';
		if (typeof Blob !== 'undefined' && body instanceof Blob) return `[binary body, ${body.size} bytes]`;
		if (body instanceof ArrayBuffer || ArrayBuffer.isView(body)) return `[binary body, ${body.byteLength} bytes]`;
		return String(body);
	}

	function headersToObject(headers) {
		const out = {};
		try {
			if (!headers) return out;
			if (typeof Headers !== 'undefined' && headers instanceof Headers) headers.forEach((value, name) => { out[name] = value; });
			else if (Array.isArray(headers)) for (const [name, value] of headers) out[name] = value;
			else for (const name of Object.keys(headers)) out[name] = String(headers[name]);
		} catch (e) { /* headers that cannot be read: keep what was read */ }
		return out;
	}

	function parseRawHeaders(raw) {
		const out = {};
		for (const line of String(raw || '').trim().split(/\r?\n/)) {
			const colon = line.indexOf(':');
			if (colon > 0) out[line.slice(0, colon).trim()] = line.slice(colon + 1).trim();
		}
		return out;
	}

	const isTextType = (type) => /json|text|xml|javascript|html|x-www-form-urlencoded|svg/i.test(type);

	function captureBegin(kind, method, url, headers, body, label) {
		const entry = { key: documentId + ':' + (++capSeq), frame: capFrame, kind, label: label || '', method, url, reqHeaders: headers, reqBody: body, tStart: epochNow(), state: 'pending' };
		capDirty.add(entry);
		capSchedule();
		return entry;
	}
	function captureChanged(entry) { capDirty.add(entry); capSchedule(); }
	function captureDone(entry, text) { entry.tEnd = epochNow(); entry.state = 'done'; entry.resBody = text; captureChanged(entry); }
	function captureFail(entry, error) {
		entry.tEnd = epochNow();
		entry.state = error && error.name === 'AbortError' ? 'aborted' : 'failed';
		entry.error = String((error && error.message) || error);
		captureChanged(entry);
	}
	function capSchedule() { if (!capTimer) capTimer = setTimeout(capFlush, 400); }
	function capFlush() {
		capTimer = null;
		if (!capDirty.size) return;
		const batch = [...capDirty].map((entry) => ({ ...entry }));
		capDirty.clear();
		capSink(batch);
	}

	function captureFetch() {
		const originalFetch = window.fetch;
		if (typeof originalFetch !== 'function') return;
		window.fetch = function (input, init) {
			let entry = null;
			try {
				const isRequest = typeof Request !== 'undefined' && input instanceof Request;
				const url = absoluteUrl(isRequest ? input.url : typeof input === 'string' ? input : (input && input.href) || String(input));
				const method = String((init && init.method) || (isRequest && input.method) || 'GET').toUpperCase();
				const headers = headersToObject((init && init.headers) || (isRequest && input.headers) || {});
				const body = init && init.body !== undefined ? bodyText(init.body) : (isRequest ? '[body of a Request object, not readable here]' : '');
				entry = captureBegin('fetch', method, url, headers, body, init && init.__snlLabel);   // __snlLabel: our own calls say what they are; fetch ignores it
			} catch (e) { entry = null; }
			let promise;
			try { promise = originalFetch.apply(this, arguments); } catch (e) { if (entry) captureFail(entry, e); throw e; }
			if (!entry) return promise;
			return promise.then((response) => {
				entry.tHeaders = epochNow();
				entry.status = response.status;
				entry.statusText = response.statusText;
				entry.resHeaders = headersToObject(response.headers);
				captureChanged(entry);
				const type = response.headers.get('content-type') || '';
				if (type && !isTextType(type)) {
					const length = response.headers.get('content-length');
					captureDone(entry, `[binary body ${type}${length ? ', ' + length + ' bytes' : ''}: not captured]`);
				} else {
					response.clone().text().then((text) => captureDone(entry, text), (e) => captureDone(entry, '[body not readable: ' + e + ']'));
				}
				return response;
			}, (error) => { captureFail(entry, error); throw error; });
		};
	}

	function captureXhr() {
		const proto = window.XMLHttpRequest && window.XMLHttpRequest.prototype;
		if (!proto) return;
		const originalOpen = proto.open, originalSend = proto.send, originalSetHeader = proto.setRequestHeader;
		proto.open = function (method, url) {
			this.__snl = { method: String(method).toUpperCase(), url: absoluteUrl(url), headers: {} };
			return originalOpen.apply(this, arguments);
		};
		proto.setRequestHeader = function (name, value) {
			if (this.__snl) this.__snl.headers[name] = value;
			return originalSetHeader.apply(this, arguments);
		};
		proto.send = function (body) {
			const meta = this.__snl;
			if (meta) {
				const entry = captureBegin('xhr', meta.method, meta.url, meta.headers, bodyText(body), '');
				let aborted = false;
				this.addEventListener('readystatechange', () => { if (this.readyState === 2 && !entry.tHeaders) { entry.tHeaders = epochNow(); entry.status = this.status; } });
				this.addEventListener('abort', () => { aborted = true; });
				this.addEventListener('loadend', () => {
					entry.status = this.status;
					entry.resHeaders = parseRawHeaders(this.getAllResponseHeaders());
					if (this.status === 0) {
						entry.tEnd = epochNow(); entry.state = aborted ? 'aborted' : 'failed'; entry.error = 'no response (network error, blocked or cancelled)';
						captureChanged(entry);
						return;
					}
					let text;
					try {
						if (this.responseType === '' || this.responseType === 'text') text = this.responseText;
						else if (this.responseType === 'json') text = JSON.stringify(this.response);
						else text = `[${this.responseType} body: not captured]`;
					} catch (e) { text = '[body not readable: ' + e + ']'; }
					captureDone(entry, text);
				});
			}
			return originalSend.apply(this, arguments);
		};
	}

	/** The page's other downloads (scripts, styles, images, the page itself): timings only, no bodies. Requests made with fetch and XHR are captured in full above. */
	function captureResources() {
		if (typeof PerformanceObserver !== 'function') return;
		const add = (list) => {
			for (const r of list.getEntries()) {
				if (r.entryType === 'resource' && (r.initiatorType === 'fetch' || r.initiatorType === 'xmlhttprequest')) continue;
				const entry = { key: documentId + ':r' + (++capSeq), frame: capFrame, kind: r.entryType, label: r.initiatorType || r.entryType, method: 'GET', url: r.name,
					tStart: performance.timeOrigin + r.startTime, tEnd: performance.timeOrigin + (r.responseEnd || r.startTime + r.duration), status: r.responseStatus || 0, state: 'done',
					bytes: r.transferSize, protocol: r.nextHopProtocol };
				capDirty.add(entry);
			}
			capSchedule();
		};
		for (const type of ['resource', 'navigation']) {
			try { new PerformanceObserver(add).observe({ type, buffered: true }); } catch (e) { /* this kind is not supported */ }
		}
	}

	/** The web address of the page around this frame if it is one we trust to take the log; otherwise null (this page then keeps its own log). */
	function trustedTopOrigin() {
		if (window.top === window) return null;
		const origins = location.ancestorOrigins;
		const origin = origins && origins.length ? origins[origins.length - 1] : '';   // the last one is the outermost page
		return origin === 'https://snlstandby.nbcuni.com' || isLocalOrigin(origin) ? origin : null;
	}

	function installCapture(frameName) {
		if (!CONFIG.LOG_ENABLED) return;
		capFrame = frameName;
		const topOrigin = trustedTopOrigin();
		capSink = topOrigin ? (batch) => window.top.postMessage({ type: MSG_LOG_BATCH, frame: frameName, entries: batch }, topOrigin) : (batch) => collectorAdd(batch);
		captureFetch();
		captureXhr();
		captureResources();
		window.addEventListener('pagehide', () => { clearTimeout(capTimer); capFlush(); });   // the last news of this page goes out before the page is gone
	}

	/** Tells the collector when the show opens (it writes the log CONFIG.LOG_AFTER_OPEN_MS later if the run has not succeeded by then). */
	function reportOpenTime(ms) {
		if (!CONFIG.LOG_ENABLED) return;
		const topOrigin = trustedTopOrigin();
		if (topOrigin) window.top.postMessage({ type: MSG_LOG_OPEN, openAtMs: ms }, topOrigin);
		else collectorSetOpen(ms);
	}

	/** Tells the collector the run succeeded: it writes the log when the calls that follow the RSVP have been answered (at most CONFIG.LOG_SETTLE_MS). */
	function signalLogDone(reason) {
		if (!CONFIG.LOG_ENABLED) return;
		const topOrigin = trustedTopOrigin();
		if (topOrigin) window.top.postMessage({ type: MSG_LOG_DONE, reason, settleMs: CONFIG.LOG_SETTLE_MS }, topOrigin);
		else collectorDone(reason, CONFIG.LOG_SETTLE_MS);
	}

	// ---------- collector (the top page; a page that is opened on its own collects for itself) ----------
	const collector = { entries: new Map(), openAtMs: null, finalized: false, afterOpenTimer: null, doneTimer: null, persistTimer: null, lastChange: Date.now(), persisted: false };

	function collectorAdd(batch) {
		if (collector.finalized) return;
		for (const entry of batch) collector.entries.set(entry.key, entry);
		collector.lastChange = Date.now();
		collectorPersistSoon();
	}

	function collectorSetOpen(ms) {
		if (collector.finalized || !Number.isFinite(ms)) return;
		collector.openAtMs = ms;
		clearTimeout(collector.afterOpenTimer);
		let wait = ms + CONFIG.LOG_AFTER_OPEN_MS - Date.now();
		if (wait < 0) wait = 30000;   // that moment has already passed (a late start): the run gets 30 s
		collector.afterOpenTimer = setTimeout(() => collectorFinalize(`${Math.round(CONFIG.LOG_AFTER_OPEN_MS / 1000)} s after the open time, without a successful RSVP`), wait);
		collectorPersistSoon();
	}

	/** The run succeeded: the log is written once every request has been answered and 1.5 s have passed without news (the calls that follow the RSVP are slow
	 *  at the go-live), or after maxMs, whichever comes first. */
	function collectorDone(reason, maxMs) {
		if (collector.finalized || collector.doneTimer) return;
		const startedAt = Date.now();
		collector.doneTimer = setInterval(() => {
			const pending = [...collector.entries.values()].some((e) => (e.kind === 'fetch' || e.kind === 'xhr') && e.state === 'pending');
			if ((!pending && Date.now() - collector.lastChange >= 1500) || Date.now() - startedAt >= maxMs) collectorFinalize(reason);
		}, 500);
	}

	/** A log is worth keeping once the run has really started: the registration page or the RSVP was seen, or the open time is (nearly) here. */
	function collectorWorthKeeping() {
		return [...collector.entries.values()].some((e) => e.frame === 'registration' || /\/attendees\/rsvp|\/load-for-visitor/.test(e.url))
			|| (collector.openAtMs !== null && Date.now() > collector.openAtMs - 5000);
	}

	function collectorPersistSoon() { if (!collector.persistTimer) collector.persistTimer = setTimeout(collectorPersist, 2000); }

	/** Keeps a copy of the log in localStorage, for when the page is left before the file was written. Smaller copies are tried if it does not fit. */
	function collectorPersist() {
		clearTimeout(collector.persistTimer);
		collector.persistTimer = null;
		if (collector.finalized) return;
		if (!collectorWorthKeeping()) {
			if (collector.persisted) { try { localStorage.removeItem(LOG_KEY); collector.persisted = false; } catch (e) { /* storage blocked */ } }   // only a copy this collector wrote itself
			return;
		}
		const shrinks = [
			(e) => e,
			(e) => ({ ...e, resBody: typeof e.resBody === 'string' && e.resBody.length > 2000 ? e.resBody.slice(0, 2000) + '...[cut to save space]' : e.resBody }),
			(e) => (e.kind === 'resource' || e.kind === 'navigation') ? null : { ...e, resBody: undefined, reqBody: undefined },
		];
		for (const shrink of shrinks) {
			try {
				const entries = [...collector.entries.values()].map(shrink).filter(Boolean);
				localStorage.setItem(LOG_KEY, JSON.stringify({ v: 1, savedAt: Date.now(), openAtMs: collector.openAtMs, entries }));
				collector.persisted = true;
				return;
			} catch (e) { /* too big, or storage blocked: try a smaller copy */ }
		}
	}

	/** The top page, when it opens: a copy left by a page that was left (or reloaded) before its log was written is taken now (so this page's own copy can not
	 *  overwrite it) and written out as soon as the page has a body to hang the download on. */
	function recoverPreviousLog() {
		let saved = null;
		try { saved = JSON.parse(localStorage.getItem(LOG_KEY) || 'null'); localStorage.removeItem(LOG_KEY); } catch (e) { return; }
		if (!saved || !Array.isArray(saved.entries) || !saved.entries.length) return;
		const reason = `recovered: the page was left or reloaded before the log was written (copy saved ${new Date(saved.savedAt).toLocaleString()})`;
		waitFor(() => document.body, 15000, 100).then((body) => {
			if (body) downloadText(logFileName(saved.savedAt, '_recovered'), buildLogText(saved.entries, saved.openAtMs, reason));
		});
	}

	/** Writes the log file. byHand = from the settings panel: the run is not ended by it. */
	function collectorFinalize(reason, byHand = false) {
		if (!byHand && collector.finalized) return;
		const ok = downloadText(logFileName(Date.now(), byHand ? '_by-hand' : ''), buildLogText([...collector.entries.values()], collector.openAtMs, reason));
		if (byHand) return ok;
		collector.finalized = true;
		clearTimeout(collector.afterOpenTimer); clearInterval(collector.doneTimer); clearTimeout(collector.persistTimer);
		try { localStorage.removeItem(LOG_KEY); } catch (e) { /* storage blocked */ }
		log('request log written: ' + reason);
		return ok;
	}

	function startLogCollector() {
		if (!CONFIG.LOG_ENABLED) return;
		if (window.top === window) {
			window.addEventListener('message', (event) => {
				const data = event.data;
				if (!data || typeof data.type !== 'string' || !data.type.startsWith('snl-log-')) return;
				if (event.origin !== 'https://pro.vow.app' && event.origin !== 'https://go.vow.app' && !isLocalOrigin(event.origin)) return;
				if (!Array.prototype.some.call(window.frames, (frame) => frame === event.source)) return;   // only from the frames of this page
				if (data.type === MSG_LOG_BATCH && Array.isArray(data.entries)) collectorAdd(data.entries.filter((e) => e && typeof e.key === 'string' && typeof e.url === 'string'));
				else if (data.type === MSG_LOG_OPEN) collectorSetOpen(Number(data.openAtMs));
				else if (data.type === MSG_LOG_DONE) collectorDone(String(data.reason || 'the run succeeded').slice(0, 200), Math.min(Math.max(Number(data.settleMs) || 0, 0), 120000));
			});
			recoverPreviousLog();
		}
		if (window.top === window || !trustedTopOrigin()) window.addEventListener('pagehide', collectorPersist);   // a frame whose entries go to the top page keeps no copy
	}

	// ---------- writing the file ----------
	function logFileName(ms, suffix) {
		const d = new Date(ms);
		const p = (n) => String(n).padStart(2, '0');
		return `snl_${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}_${p(d.getHours())}-${p(d.getMinutes())}-${p(d.getSeconds())}${suffix || ''}.log`;
	}

	/** Saves text as a file by clicking a temporary download link. Done by the collecting page (the top page), where downloads are not blocked. */
	function downloadText(filename, text) {
		try {
			const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
			const link = document.createElement('a');
			link.href = url; link.download = filename; link.style.display = 'none';
			(document.body || document.documentElement).appendChild(link);
			link.click();
			setTimeout(() => { link.remove(); URL.revokeObjectURL(url); }, 10000);
			log(`log file written to the downloads: ${filename} (${text.length} characters)`);
			return true;
		} catch (e) { log('could not write the log file', e); return false; }
	}

	/** The text of the log: a header, every request and response (and file download) in the order they happened, and a summary. The layout follows VowTickets' log. */
	function buildLogText(entries, openAtMs, reason) {
		const pad = (n, width = 2) => String(n).padStart(width, '0');
		const stamp = (ms) => { const d = new Date(Math.round(ms)); return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`; };
		const headerLines = (headers) => Object.keys(headers || {}).map((name) => `${name}: ${headers[name]}`);
		const writtenAt = Date.now();
		const sorted = [...entries].sort((a, b) => a.tStart - b.tStart);
		const events = [];
		const lastBody = new Map();   // "METHOD url" -> { id, body } of the latest answered request, so a body that repeats is not written again
		const slowest = [];
		const noAnswer = [];
		let number = 0, requests = 0, files = 0;
		for (const e of sorted) {
			if (e.kind === 'fetch' || e.kind === 'xhr') {
				requests++;
				const id = ++number;
				const tag = `[${e.frame} ${e.kind}${e.label ? ', ' + e.label : ''}]`;
				events.push({ t: e.tStart, text: [`${stamp(e.tStart)} REQUEST #${id} ${tag}`, `${e.method} ${e.url}`, ...headerLines(e.reqHeaders), '', e.reqBody || ''].join('\n') });
				if (e.state === 'done') {
					const ms = Math.round(e.tEnd - e.tStart);
					slowest.push({ ms, text: `#${id} ${e.method} ${e.url} -> ${e.status}` });
					let body = e.resBody === undefined ? '' : e.resBody;
					const key = e.method + ' ' + e.url;
					const previous = lastBody.get(key);
					if (previous && previous.body === body && body.length > 200) body = `[same body as RESPONSE #${previous.id}]`;
					else lastBody.set(key, { id, body });
					const toHeaders = e.tHeaders ? `   (headers after ${Math.round(e.tHeaders - e.tStart)} ms)` : '';
					events.push({ t: e.tEnd, text: [`${stamp(e.tEnd)} RESPONSE #${id} after ${ms} ms${toHeaders}`, `HTTP ${e.status}${e.statusText ? ' ' + e.statusText : ''}`, ...headerLines(e.resHeaders), '', body].join('\n') });
				} else if (e.state === 'failed' || e.state === 'aborted') {
					const ms = Math.round(e.tEnd - e.tStart);
					events.push({ t: e.tEnd, text: `${stamp(e.tEnd)} NO RESPONSE #${id} after ${ms} ms (${e.state === 'aborted' ? 'cancelled' : 'failed'}): ${e.error || ''}` });
					noAnswer.push(`#${id} ${e.method} ${e.url} (${e.state === 'aborted' ? 'cancelled' : 'failed'} after ${ms} ms)`);
				} else {
					const ms = Math.round(writtenAt - e.tStart);
					events.push({ t: writtenAt, text: `${stamp(writtenAt)} NO RESPONSE #${id}: nothing had been received after ${ms} ms when the log was written (the page may have been left)` });
					noAnswer.push(`#${id} ${e.method} ${e.url} (no answer after ${ms} ms)`);
				}
			} else {
				files++;
				const ms = Math.round(e.tEnd - e.tStart);
				slowest.push({ ms, text: `${e.label} ${e.url}` });
				events.push({ t: e.tEnd, text: `${stamp(e.tEnd)} FILE [${e.frame} ${e.label}] ${e.url}  HTTP ${e.status || '?'}  ${ms} ms  ${e.bytes != null ? e.bytes + ' bytes  ' : ''}${e.protocol || ''}` });
			}
		}
		events.sort((a, b) => a.t - b.t);
		slowest.sort((a, b) => b.ms - a.ms);
		const first = sorted.length ? stamp(sorted[0].tStart) : '-';
		const last = events.length ? stamp(events[events.length - 1].t) : '-';
		return [
			'SNL Standby request log (SnlStandby.user.js)',
			`Written:     ${stamp(writtenAt)}, because: ${reason}`,
			`Show opens:  ${openAtMs ? stamp(openAtMs) : 'not known'}`,
			`Covers:      ${first}  to  ${last}`,
			`Contents:    ${requests} requests (fetch / XHR, with headers and whole bodies) and ${files} file downloads (timings only)`,
			'Times are this computer\'s local time. A REQUEST and its RESPONSE share a number; FILE lines are downloads the pages made (scripts, styles, images, the page itself).',
			'Only headers the browser lets a page read are here: no cookies, and cross-origin answers show only the headers the server exposes.',
			'',
			...events.map((ev) => ev.text + '\n'),
			'=== Summary ===',
			'Slowest:',
			...slowest.slice(0, 10).map((s) => `  ${(s.ms / 1000).toFixed(2)} s  ${s.text}`),
			noAnswer.length ? 'No answer:' : 'No answer: none',
			...noAnswer.map((s) => '  ' + s),
			'',
		].join('\n');
	}

	// =====================================================================
	// ==============  RSVP RESPONSE RECORDER (stage 2 only)  ==============
	// =====================================================================
	// Wraps window.fetch to keep a copy of the RSVP request and response. The success response has never been captured, so this is how to get it:
	// after a run, in the console (F12) type   localStorage.snlRsvpLog   and keep/paste the result.
	// Set when the page's own call for the journey (load-for-visitor) fails; read while waiting for the registration page (step A).
	let journeyFailure = null;

	function installRsvpRecorder() {
		const originalFetch = window.fetch;
		window.fetch = async function (input, init) {
			const url = typeof input === 'string' ? input : (input && input.url) || String(input);
			const isRsvp = /\/attendees\/rsvp/.test(url);
			const isJourney = /\/journeys\/\d+\/load-for-visitor/.test(url);
			let response;
			try {
				response = await originalFetch.apply(this, arguments);
			} catch (error) {
				if (isJourney) journeyFailure = 'network error';
				if (isRsvp) recordRsvp({ time: new Date().toISOString(), url, status: 0, requestBody: init && init.body, responseBody: 'network error: ' + error });
				throw error;
			}
			if (isJourney && !response.ok) journeyFailure = `HTTP ${response.status}`;
			if (isRsvp) {
				try {
					const body = await response.clone().text();
					recordRsvp({ time: new Date().toISOString(), url, status: response.status, requestBody: init && init.body, responseBody: body.slice(0, 5000) });
				} catch (e) { log('could not record RSVP response', e); }
			}
			return response;
		};
	}

	let rsvpAnswered = false;
	let rsvpWatch = null;
	/** After SUBMIT: until the RSVP is answered, the banner says how long we have been waiting. The script never cancels or resends the RSVP. */
	function watchRsvpAnswer() {
		const sentAt = Date.now();
		rsvpWatch = setInterval(() => {
			if (rsvpAnswered) { clearInterval(rsvpWatch); return; }
			const waited = Date.now() - sentAt;
			if (waited >= CONFIG.RSVP_SLOW_NOTICE_MS) banner(`No answer from the RSVP after ${Math.round(waited / 1000)} s. It may still be processing. Do NOT click SUBMIT again: wait, and check the page and your email. The script will not send it again.`, 'warn', true);
		}, 1000);
	}

	/** For an RSVP that did not succeed: the script never sends it again; this says how to try again by hand. */
	function noResendNote() {
		const uuid = (location.pathname.match(/\/event\/([^/]+)/) || [])[1] || 'unknown';
		return ` The script will not send it again. You can click SUBMIT on the form yourself, or let the script try once more: run  sessionStorage.removeItem('snlSubmitted_${uuid}')  in the console and reload the page.`;
	}

	let showOpenedAt = null;   // when the show opened (ms), as the list page measured it: handed over in the address, see readOpenTime

	/** The open time (ms) the list page put in the address (#snl-open=...). It is kept in sessionStorage so a reload, or the page changing its own address, does not lose it. */
	function readOpenTime(uuid) {
		const key = 'snlOpenAt_' + uuid;
		const found = location.hash.match(/snl-open=(\d{10,})/);
		try {
			if (found) sessionStorage.setItem(key, found[1]);
			const saved = Number(sessionStorage.getItem(key));
			return saved > 0 ? saved : null;
		} catch (e) { return found ? Number(found[1]) : null; }   // storage blocked
	}

	/** "12.4 s" or "1 min 12.4 s" */
	function formatDuration(ms) {
		const seconds = Math.abs(ms) / 1000;
		const text = seconds >= 60 ? `${Math.floor(seconds / 60)} min ${(seconds % 60).toFixed(1)} s` : `${seconds.toFixed(1)} s`;
		return ms < 0 ? '-' + text : text;
	}

	function recordRsvp(entry) {
		rsvpAnswered = true;
		clearInterval(rsvpWatch);
		if (showOpenedAt) entry.msAfterOpen = Date.now() - showOpenedAt;   // kept in the log too
		(window.__snlRsvpLog = window.__snlRsvpLog || []).push(entry);
		try { localStorage.setItem('snlRsvpLog', JSON.stringify(window.__snlRsvpLog)); } catch (e) { /* storage blocked */ }
		log('RSVP response', entry);
		if (entry.status >= 200 && entry.status < 300) {
			signalLogDone('RSVP accepted');   // the request log is written once the calls that follow have been answered
			const took = entry.msAfterOpen !== undefined ? formatDuration(entry.msAfterOpen) : null;
			banner(`RSVP accepted (HTTP ${entry.status})${took ? ': ' + took + ' after the show opened' : ''}. Check the page for the confirmation number.`, 'ok', false,
				`RSVP accepted (HTTP ${entry.status}). ` +
				(took ? `<span style="font:700 22px/1.2 Consolas,monospace;background:rgba(0,0,0,.3);border-radius:4px;padding:1px 8px">${esc(took)}</span> after the show opened. ` : '') +
				'Check the page for the confirmation number.');
		}
		else if (entry.status === 422 && /capacity_full/.test(entry.responseBody)) banner('The event is FULL (HTTP 422).', 'error');
		else if (entry.status === 429) banner('Rate limited (HTTP 429). Do not keep clicking; wait.' + noResendNote(), 'error');
		else if (entry.status === 0) banner('The RSVP request did not get an answer (network error). It may or may not have reached the server; check your email and the page before trying again.' + noResendNote(), 'error');
		else banner(`RSVP failed: HTTP ${entry.status} ${entry.responseBody.slice(0, 200)}.` + noResendNote(), 'error');
	}

	// =====================================================================
	// ==================  STAGE 1: the show list (pro.vow.app)  ===========
	// =====================================================================
	// Speeding up the page's own 20 s refresh (the "Register Now" button only appears when the page itself reloads the list). We leave the page's
	// timer alone: our own rapid polling (see listStage) finds the open show, and then, depending on CONFIG.SPEEDUP ('auto' picks the best one the
	// page allows, see bestSpeedup; or force 0 or 1):
	//   0. No change needed: we navigate to register_url ourselves as soon as our own poll sees "open" (go() below), so the page's button is
	//      never needed. Only the on-screen list stays up to 20 s behind.
	//   1. Feed our finding into the page:   vm.$set(vm, 'events', json.events)  when our poll finds the chosen show open (not on the polls
	//      before that), then follow the page's own "Register Now" link. If $set cannot be done, or no link shows within PUSH_WAIT_MS, we
	//      redirect to register_url ourselves (mode 0). No extra requests.
	// The list page is a Vue 2 component (.nbc-page) with data { events, pollTimer } and a method load({silent}). Because @grant none runs us in
	// the page's own JS world, the component is reachable as  document.querySelector('.nbc-page').__vue__  (call it vm).
	// Cautions: wait for .nbc-page to exist (it mounts after we start); .nbc-page and $set are Vue's / the site's internal names and can change
	// with a rebuild, so check they exist. The rate limit of 10 is on the RSVP request only (docs/VOW_SNL_FLOW.md 5.4); the show list returned no
	// limit headers and was not throttled at about 3 requests per second (2,619 requests on 2026-10-01), so extra list fetches do not use it up.
	/** The list page's Vue component (see the comment above), or null if it is not there (yet) or does not have the events list we expect. */
	function pageVm() {
		const el = document.querySelector('.nbc-page');
		const vm = el && el.__vue__;
		return vm && Array.isArray(vm.events) ? vm : null;
	}

	/** What the page's component allows right now: { mode: 1 | 0, why } (the best speed-up mode, and the reason). */
	function bestSpeedup() {
		const vm = pageVm();
		if (!vm) return { mode: 0, why: 'the page component (.nbc-page) with its events list was not found' };
		if (typeof vm.$set === 'function') return { mode: 1, why: 'the page component has events and $set()' };
		return { mode: 0, why: 'the page component has no $set()' };
	}

	let pageVmWarned = false;
	/** Speed-up mode 1: runs action(vm) on the page component. Returns true if it ran without an error; logs once if the component cannot be found. */
	function usePageVm(action) {
		const vm = pageVm();
		if (!vm) {
			if (!pageVmWarned) { pageVmWarned = true; log('speed-up mode 1: the page component (.nbc-page) was not found, so the page was not refreshed'); }
			return false;
		}
		try { action(vm); return true; } catch (e) { log('speed-up mode 1 failed', e); return false; }
	}

	/**
	 * TESTING: asks the local replay server to open the show list TEST_OPEN_IN_SECONDS from now (see CONFIG). Used only on localhost.
	 * Returns the open time to use instead of the weekly one, or null when the setting is null or this is not localhost.
	 */
	async function armTestOpenTime() {
		const seconds = testOpenInSeconds;   // null unless isLocal
		if (seconds === null) {
			if (CONFIG.TEST_OPEN_IN_SECONDS !== null) log(`TEST_OPEN_IN_SECONDS is ignored: this is ${location.hostname}, not localhost`);
			return null;
		}
		try {
			const response = await fetch(`/__replay/open-in/${seconds}`, { cache: 'no-store', __snlLabel: 'test control (script)' });
			const text = await response.text();
			if (!response.ok) throw new Error(`HTTP ${response.status} ${text.slice(0, 100)}`);
			log(`TEST: ${text}`);
		} catch (e) {
			await banner(`TEST: could not set the open time on the server (is this the replay server?): ${e.message || e}`, 'warn');
		}
		return new Date(Date.now() + seconds * 1000);
	}

	/** The next CONFIG.OPEN_WEEKDAY at OPEN_HOUR:OPEN_MINUTE (local time); today's if that was less than GIVE_UP_AFTER_MIN ago. */
	function nextOpenTime(now = new Date()) {
		const t = new Date(now);
		t.setHours(CONFIG.OPEN_HOUR, CONFIG.OPEN_MINUTE, 0, 0);
		t.setDate(t.getDate() + (CONFIG.OPEN_WEEKDAY - t.getDay() + 7) % 7);
		if (t.getTime() < now.getTime() - CONFIG.GIVE_UP_AFTER_MIN * 60000) t.setDate(t.getDate() + 7);
		return t;
	}

	/** "6 days 23:59:12" */
	function formatCountdown(ms) {
		const total = Math.max(0, Math.ceil(ms / 1000));
		const days = Math.floor(total / 86400);
		const pad = (n) => String(n).padStart(2, '0');
		return `${days ? days + (days === 1 ? ' day ' : ' days ') : ''}${pad(Math.floor(total % 86400 / 3600))}:${pad(Math.floor(total % 3600 / 60))}:${pad(total % 60)}`;
	}

	/** The titles of the shows the list page is showing (read from the page, nothing is sent). */
	function pageShowTitles() {
		return [...document.querySelectorAll(SEL.card)].map((card) => ((card.querySelector(SEL.cardTitle) || {}).textContent || '').trim()).filter(Boolean);
	}

	/** The href of the chosen show's "Register Now" link on the list page, or null. */
	function pageRegisterLink(show) {
		for (const card of document.querySelectorAll(SEL.card)) {
			const title = (card.querySelector(SEL.cardTitle) || {}).textContent || '';
			const link = card.querySelector(SEL.cardRegisterLink);
			if (show.re.test(title) && link && link.href) return link.href;
		}
		return null;
	}

	async function listStage() {
		const show = SHOWS[CONFIG.SHOW];
		let lastWaitingLog = '';
		let waitingNote = '';   // the latest poll result, shown under the countdown once the rapid polling has started
		let nameProblem = '';   // set when the page lists shows but none matches the chosen show (checked during the wait, so there is time to fix it)
		let lastNameCheck = 0;
		let listErrors = 0;     // rapid polling: list requests in a row that failed or got no answer
		let listErrorText = '';
		let goingTo = null;
		// The speed-up mode in use. With 'auto' it is decided from what the page offers: during the countdown (so it shows on screen long before
		// the open) and again when the rapid polling starts.
		let speedup = CONFIG.SPEEDUP === 'auto' ? 0 : CONFIG.SPEEDUP;
		let speedupDecided = CONFIG.SPEEDUP !== 'auto';
		let speedupWhy = speedupDecided ? 'set in CONFIG' : 'checking the page...';
		log(`speed-up: ${CONFIG.SPEEDUP}`);
		// the testing override is armed before anything is polled, so a leftover "open" from an earlier test is not seen
		let testOpenAt = await armTestOpenTime();
		let openAt = testOpenAt || nextOpenTime();
		let deadline = openAt.getTime() + CONFIG.GIVE_UP_AFTER_MIN * 60000;
		let rapidFrom = openAt.getTime() - CONFIG.RAPID_POLL_LEAD_SECONDS * 1000;
		log(`open time ${openAt.toString()}${testOpenAt ? ' (TEST)' : ''}; rapid polling from ${new Date(rapidFrom).toLocaleTimeString()}`);
		reportOpenTime(openAt.getTime());   // the request log is written LOG_AFTER_OPEN_MS after this if the run has not succeeded by then

		// Localhost only: the "Set test open time" button in the settings panel can move the open time while this runs (see the panel). The countdown,
		// the start of the rapid polling, the watchdog and the give-up time all read these variables, so they follow. If the rapid polling has already
		// started it just carries on until the new open time (the countdown is not shown again).
		setOpenTime = (openAtMs) => {
			if (!Number.isFinite(openAtMs)) return false;
			openAt = new Date(openAtMs);
			testOpenAt = openAt;
			deadline = openAt.getTime() + CONFIG.GIVE_UP_AFTER_MIN * 60000;
			rapidFrom = openAt.getTime() - CONFIG.RAPID_POLL_LEAD_SECONDS * 1000;
			log(`open time changed to ${openAt.toString()} (TEST); rapid polling from ${new Date(rapidFrom).toLocaleTimeString()}`);
			reportOpenTime(openAt.getTime());
			return true;
		};
		if (isLocal) {
			window.addEventListener('message', (event) => {   // the settings panel in the page around this frame asks for the change
				const data = event.data;
				if (!data || data.type !== MSG_OPEN_TIME || event.source !== window.top || !isLocalOrigin(event.origin)) return;
				if (setOpenTime(Number(data.openAtMs))) event.source.postMessage({ type: MSG_OPEN_TIME_ACK, openAtMs: data.openAtMs }, event.origin);
			});
		}

		/** Compares the shows the page lists (coming soon ones too) with the chosen show. Looks at the page only, about once a second. */
		function checkShowNames() {
			if (Date.now() - lastNameCheck < 1000) return;
			lastNameCheck = Date.now();
			const titles = pageShowTitles();
			const problem = titles.length && !titles.some((title) => show.re.test(title))
				? `None of the shows on the page matches "${show.label}" (the page lists: ${titles.map((t) => `"${t}"`).join(', ')}). Choose the right show in the settings panel (top right) and reload the page.`
				: '';
			if (problem !== nameProblem) { nameProblem = problem; log(problem || 'the show names on the page match the chosen show again'); }
		}

		function go(url, how) {
			if (goingTo) return;
			goingTo = url;
			banner(`"${show.label}" is open (${how}). Opening the registration page...`, 'ok');
			// the registration page is another web address (its own sessionStorage), so the open time travels in the address: #snl-open=<ms>.
			// The registration page reads it to show how long after the show opened the RSVP was accepted.
			let target = url;
			try { const u = new URL(url); u.hash = 'snl-open=' + openAt.getTime(); target = u.href; } catch (e) { /* keep the plain address */ }
			navigateWithRetry(target);
		}

		/** Goes to the registration page. The list page (and this script) stays alive until the new page starts to arrive, so if nothing has arrived
		 *  after CONFIG.NAVIGATE_TIMEOUT_MS the navigation is started again (a new navigation cancels the stuck one), CONFIG.NAVIGATE_ATTEMPTS tries in all.
		 *  Once the new page arrives this document is gone and the loop stops with it. Nothing has been submitted yet, so repeating is safe. */
		async function navigateWithRetry(url) {
			const tries = CONFIG.NAVIGATE_ATTEMPTS;
			for (let attempt = 1; attempt <= tries; attempt++) {
				location.assign(url);
				await sleep(CONFIG.NAVIGATE_TIMEOUT_MS);
				if (attempt < tries) {
					log(`the registration page did not start to arrive within ${CONFIG.NAVIGATE_TIMEOUT_MS / 1000} s (try ${attempt} of ${tries}); asking again`);
					await banner(`The registration page did not answer within ${CONFIG.NAVIGATE_TIMEOUT_MS / 1000} s. Trying again (try ${attempt + 1} of ${tries})...`, 'warn');
				}
			}
			await banner(`The registration page did not answer after ${tries} tries. Reload this page, or open ${url} yourself.`, 'error');
		}

		/** Redraws the status box: the show, the countdown to the open time, the speed-up mode, and what the script is doing. rapid = the rapid polling has
		 *  started. The countdown runs on in the rapid polling phase, until the open time. Screen only (quiet): the console gets lines when things change. */
		async function drawStatus(rapid) {
			if (goingTo) return;   // do not paint over the "is open, opening the registration page" message
			const msLeft = openAt.getTime() - Date.now();
			const opensAt = openAt.toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit' });
			const left = formatCountdown(msLeft);
			const speedupText = speedupDecided ? `mode ${speedup} (${speedupWhy})` : speedupWhy;
			const modeColor = { 1: '#f9a825', 0: '#546e7a' }[speedup];
			const reached = msLeft <= 0;
			let doing;   // the last line: what is going on now
			if (!rapid) doing = `Rapid polling starts ${CONFIG.RAPID_POLL_LEAD_SECONDS} s before opening.`;
			else if (reached) doing = `Open time reached. ${waitingNote || 'Waiting for the page to show the link...'}`;
			else doing = `Rapid polling: ${waitingNote || 'asking the list ourselves...'}`;
			const warnings = [];   // things to fix or look at, shown under the rest
			if (nameProblem) warnings.push(nameProblem);
			if (rapid && listErrors >= 2) warnings.push(`The show list is not answering properly: ${listErrorText} (${listErrors} requests in a row).`);
			await banner(
				`"${show.label}" opens ${opensAt}${testOpenAt ? ' (TEST)' : ''}\nCountdown  ${left}\nSpeed-up: ${speedupText}\n${doing}${warnings.map((w) => '\n' + w).join('')}`,
				warnings.length ? 'warn' : 'info', true,
				`<div style="font-size:13px"><b>${esc(show.label)}</b> opens ${esc(opensAt)}${testOpenAt ? ' ' + chip('TEST', '#ffd54f', '#222') : ''}</div>` +
				`<div style="margin:3px 0"><span style="opacity:.85">Countdown</span> <span style="font:700 26px/1.15 Consolas,monospace;letter-spacing:1px;background:rgba(0,0,0,.28);border-radius:4px;padding:0 8px">${esc(left)}</span>` +
				`${rapid ? ' ' + chip(reached ? 'OPEN TIME REACHED' : 'RAPID POLLING', reached ? '#2e7d32' : '#b26a00') : ''}</div>` +
				`<div>Speed-up: ${speedupDecided ? chip('mode ' + speedup, modeColor) + ' <span style="opacity:.85">' + esc(speedupWhy) + '</span>' : '<span style="opacity:.85">' + esc(speedupWhy) + '</span>'}</div>` +
				`<div style="opacity:.85">${esc(doing)}</div>` +
				warnings.map((w) => `<div style="margin-top:4px;padding:3px 6px;background:rgba(0,0,0,.35);border-left:4px solid #ff5252;border-radius:3px"><b>Check this:</b> ${esc(w)}</div>`).join(''));
		}

		// ---- before the rapid polling: only a countdown on screen (and the cheap look at the page below, which sends nothing) ----
		const stageStart = Date.now();
		let lastCapabilityCheck = 0;
		let lastCountdownLog = 0;
		keepOpenWarning = true;   // the warning is shown during this first wait only
		while (Date.now() < rapidFrom && !goingTo) {
			const link = pageRegisterLink(show);
			if (link) { go(link, 'page'); break; }
			checkShowNames();
			if (CONFIG.SPEEDUP === 'auto' && Date.now() - lastCapabilityCheck >= 2000) {
				lastCapabilityCheck = Date.now();
				const best = bestSpeedup();
				// not finding the component right after the page loaded is not an answer yet (the page's own code may still be starting)
				if (best.mode !== 0 || Date.now() - stageStart >= CONFIG.CAPABILITY_WAIT_MS) {
					if (!speedupDecided || best.mode !== speedup) log(`speed-up auto: mode ${best.mode} (${best.why})`);
					speedup = best.mode; speedupWhy = 'auto: ' + best.why; speedupDecided = true;
				}
			}
			// the box on screen is redrawn every 250 ms; the console gets one line a minute
			if (Date.now() - lastCountdownLog >= 60000) { 
				lastCountdownLog = Date.now(); 
				log(`countdown: ${formatCountdown(openAt.getTime() - Date.now())} to the open time`);
			}
			await drawStatus(false);
			await sleep(250);
		}
		keepOpenWarning = false;
		if (!goingTo) {
			if (CONFIG.SPEEDUP === 'auto') {   // the latest look at the page decides
				const best = bestSpeedup();
				speedup = best.mode; speedupWhy = 'auto: ' + best.why; speedupDecided = true;
				log(`speed-up auto: mode ${speedup} (${best.why})`);
			}
			log(`Rapid polling started (speed-up mode ${speedup}). Waiting for "${show.label}" to open...`);
			await drawStatus(true);
		}

		// ---- the rapid polling ----
		// A new list request every FAST_POLL_MS, started by a timer and NOT awaited, so a slow or hung request cannot hold up the next one. Each request
		// is cancelled after POLL_TIMEOUT_MS, at most MAX_POLLS_IN_FLIGHT are out at once, and the first answer that shows the show open wins: the
		// others are cancelled. An older request that answers late never overwrites what a newer one put on screen.
		const polls = new Set();   // the AbortController of every request in flight
		let found = false;         // a winner has been picked
		let newestShown = 0;       // start time of the newest request whose answer was put on screen
		function abortPolls(keep) { for (const controller of polls) if (controller !== keep) controller.abort(); }

		/** The answer of one list request (startedAt = when it was sent; controller = its own, which stays alive while the winner is handled). */
		async function handleList(events, startedAt, controller) {
			if (found || goingTo) return;
			listErrors = 0;   // an answer arrived
			const event = events.find((e) => show.re.test(e.name || ''));
			if (event && event.status === 'open' && event.register_url) {
				found = true;
				abortPolls(controller);   // the first answer that shows the show open wins; the other requests are not needed
				log(`"${event.name}" is open (the answer to the request sent ${Date.now() - startedAt} ms ago)`);
				if (speedup === 1) {
					// mode 1: show the page what we found and follow its own link; if that cannot be done, redirect ourselves (mode 0)
					const pushed = usePageVm((vm) => {
						if (typeof vm.$set !== 'function') throw new Error('the page component has no $set()');
						vm.$set(vm, 'events', events);
						log('speed-up mode 1: pushed the events into the page');
					});
					if (pushed) {
						const pageLink = await waitFor(() => pageRegisterLink(show), CONFIG.PUSH_WAIT_MS, 25);
						if (pageLink) go(pageLink, 'page, after pushing the events');
						else log(`speed-up mode 1: no link on the page ${CONFIG.PUSH_WAIT_MS} ms after the push; redirecting with register_url`);
					}
				}
				go(event.register_url, 'API');
				return;
			}
			if (startedAt < newestShown) return;   // an older request answering late: what is on screen is newer
			newestShown = startedAt;
			let waiting;
			if (!event) waiting = `Waiting... no event named like "${show.label}" in the list yet.`;
			else {
				const seats = event.capacity != null ? ` (${event.attending_count}/${event.capacity})` : '';   // the Oct 1 list has no seat counts
				waiting = `Waiting for "${event.name}": status "${event.status}"${seats}.`;
			}
			if (waiting !== lastWaitingLog) { lastWaitingLog = waiting; log(waiting); }   // the console gets a line only when the status changes
			waitingNote = event ? `${waiting} Last check ${new Date().toLocaleTimeString()}` : waiting;
		}

		function startPoll() {
			if (found || goingTo || Date.now() >= deadline) return;
			if (polls.size >= CONFIG.MAX_POLLS_IN_FLIGHT) return;   // every slot is taken by a slow request: wait for one to answer or time out
			const controller = new AbortController();
			polls.add(controller);
			const startedAt = Date.now();
			const timer = setTimeout(() => controller.abort(), CONFIG.POLL_TIMEOUT_MS);
			(async () => {
				const response = await fetch(LIST_API, { headers: { Accept: 'application/json' }, credentials: 'omit', signal: controller.signal, __snlLabel: 'list poll (script)' });
				if (!response.ok) throw new Error(`HTTP ${response.status}`);
				const events = (await response.json()).events || [];   // the same signal also cuts off an answer that never finishes
				await handleList(events, startedAt, controller);
			})().catch((e) => {
				if (controller.signal.aborted) {
					if (!found && !goingTo) { listErrors++; listErrorText = `no answer within ${CONFIG.POLL_TIMEOUT_MS / 1000} s`; log(`list check: ${listErrorText}, cancelled`); }
					return;
				}
				listErrors++; listErrorText = (e && e.message) || String(e);   // "HTTP 403" and the like
				log('list check failed', e);
			}).finally(() => { clearTimeout(timer); polls.delete(controller); });
		}

		let pollTimer = null;
		if (!goingTo) { startPoll(); pollTimer = setInterval(startPoll, CONFIG.FAST_POLL_MS); }
		while (Date.now() < deadline && !goingTo) {
			// the page itself (its own refresh, or what we pushed into it): a card for the chosen show that has a "Register Now" link
			const link = pageRegisterLink(show);
			if (link) go(link, 'page');
			checkShowNames();
			if (!goingTo) await drawStatus(true);   // every pass: the countdown keeps running until the open time
			await sleep(250);
		}
		clearInterval(pollTimer);
		abortPolls();
		if (!goingTo) await banner(`Gave up after ${CONFIG.GIVE_UP_AFTER_MIN} minutes.`, 'warn');
	}

	// =====================================================================
	// ============  STAGE 2: the registration page (go.vow.app)  ==========
	// =====================================================================
	/** Why the registration page is not going to load, or null. (The page's own journey call failed, or it shows its error screen.) */
	function registrationFailure() {
		if (journeyFailure) return `the journey call failed (${journeyFailure})`;
		const text = pageText();
		if (/no longer available|link has expired/i.test(text)) return 'the page says the link is no longer available';
		if (/just a moment|checking your browser|verify(ing)? you are (a )?human|attention required|enable javascript and cookies/i.test(text)) return 'the page shows a Cloudflare check ("verify you are human")';
		if (document.querySelector('input[type="password"]')) return 'the page shows a sign-in form';
		if (/\b(502 bad gateway|503 service (temporarily )?unavailable|504 gateway time-?out)\b|error 10\d\d|access denied/i.test(text)) return 'the page shows a server error page';
		return hungScripts();
	}

	// The browser's list of finished downloads (resource timing) keeps 250 entries by default, and a full list would make finished files look unfinished.
	// So the list is made bigger, and if it is ever full (or cannot be changed) hungScripts has no opinion.
	let resourceTimingFull = false;
	function prepareResourceTiming() {
		try {
			performance.setResourceTimingBufferSize(1500);
			performance.addEventListener('resourcetimingbufferfull', () => { resourceTimingFull = true; });
		} catch (e) { resourceTimingFull = true; }
	}

	/**
	 * The registration page's own script files that have still not finished loading CONFIG.SCRIPT_LOAD_TIMEOUT_MS after the page started, as text; or null.
	 * Only standard browser facts are used, no names from the site's code: every <script src> in the page's HTML that comes from the page's own web address
	 * must have an entry in performance.getEntriesByName(src) once it has finished loading (a file that hangs has none). Null means "no opinion" and
	 * is also what is returned when anything is unexpected (no such scripts, a full timing list), so a change in the site can at worst lose this early
	 * reload, never cause a wrong one.
	 */
	function hungScripts() {
		const age = performance.now();   // since this page started to load
		if (resourceTimingFull || age < CONFIG.SCRIPT_LOAD_TIMEOUT_MS) return null;
		const sources = [...document.querySelectorAll('script[src]')].map((el) => el.src).filter((src) => {
			try { return new URL(src).origin === location.origin; } catch (e) { return false; }
		});
		if (!sources.length) return null;
		const unfinished = sources.filter((src) => performance.getEntriesByName(src).length === 0);
		if (!unfinished.length) return null;
		return `${unfinished.length} of the page's ${sources.length} script files had not finished loading after ${Math.round(age / 1000)} s (${unfinished[0].split('/').pop().split('?')[0]}${unfinished.length > 1 ? ', ...' : ''})`;
	}

	/** Reload the registration page after a failure, up to CONFIG.RELOAD_ATTEMPTS tries in all, CONFIG.RELOAD_WAIT_MS apart. */
	async function reloadAfterFailure(reason, key) {
		let tries = 1;   // the load that just failed
		try {
			const saved = JSON.parse(sessionStorage.getItem(key) || 'null');
			if (saved && Date.now() - saved.t < 5 * 60000) tries = saved.n;
		} catch (e) { /* storage blocked */ }
		if (tries >= CONFIG.RELOAD_ATTEMPTS) {
			try { sessionStorage.removeItem(key); } catch (e) { /* storage blocked */ }
			return banner(`The registration page failed (${reason}) and all ${CONFIG.RELOAD_ATTEMPTS} tries are used. Giving up. Reload the page yourself to try again.`, 'error');
		}
		let stored = false;
		try {
			sessionStorage.setItem(key, JSON.stringify({ n: tries + 1, t: Date.now() }));
			stored = JSON.parse(sessionStorage.getItem(key)).n === tries + 1;
		} catch (e) { /* storage blocked */ }
		// without a stored count every reload would count as the first try and never stop
		if (!stored) return banner(`The registration page failed (${reason}), but the tries cannot be counted here (sessionStorage is blocked), so it is NOT reloaded.`, 'error');
		await banner(`The registration page failed (${reason}). That was try ${tries} of ${CONFIG.RELOAD_ATTEMPTS}. Reloading in ${CONFIG.RELOAD_WAIT_MS / 1000} s...`, 'warn');
		await sleep(CONFIG.RELOAD_WAIT_MS);
		location.reload();
	}

	async function registerStage() {
		const show = SHOWS[CONFIG.SHOW];
		const eventUuid = (location.pathname.match(/\/event\/([^/]+)/) || [])[1] || 'unknown';
		const submittedKey = 'snlSubmitted_' + eventUuid;
		installRsvpRecorder();
		showOpenedAt = readOpenTime(eventUuid);
		prepareResourceTiming();
		await banner('Registration page found. Waiting for the page to load...');

		// ---- Step A: get to the form. The page opens on the landing page; the form is the next step. ----
		// (a failure of the page, see registrationFailure, ends the wait too and the page is reloaded)
		const reloadKey = 'snlRegTries_' + eventUuid;
		const first = await waitFor(() => document.querySelector(SEL.landingButton) || document.querySelector(SEL.input) || registrationFailure(), CONFIG.STEP_TIMEOUT_MS);
		if (!first) return reloadAfterFailure(`nothing appeared within ${CONFIG.STEP_TIMEOUT_MS / 1000} s`, reloadKey);
		if (typeof first === 'string') return reloadAfterFailure(first, reloadKey);
		try { sessionStorage.removeItem(reloadKey); } catch (e) { /* storage blocked */ }   // the page loaded: forget the tries
		if (looksClosed()) return banner('The page says registration is CLOSED. Stopping.', 'error');

		// Wrong-show guard for when this page was opened directly: stop if the page names the OTHER show.
		if (wrongShow(show)) return banner(`This page is for the other show, not "${show.label}". Stopping.`, 'error');

		if (!document.querySelector(SEL.input)) {
			await banner('Clicking "BOOK STANDBY RESERVATION"...');
			clickButton(document.querySelector(SEL.landingButton));
		}

		// ---- Step B: fill in the form ----
		const inputs = await waitFor(() => { const list = document.querySelectorAll(SEL.input); return list.length >= 3 ? list : null; }, CONFIG.STEP_TIMEOUT_MS);
		if (!inputs) return banner('The form (First Name / Last Name / Email) did not appear.', 'error');
		if (looksClosed()) return banner('The page says registration is CLOSED. Stopping.', 'error');
		if (wrongShow(show)) return banner(`This form is for the other show, not "${show.label}". Stopping.`, 'error');
		await banner('Filling in the form...');

		const values = { first: CONFIG.FIRST_NAME.trim(), last: CONFIG.LAST_NAME.trim(), email: CONFIG.EMAIL.trim() };
		const byLabel = { first: null, last: null, email: null };
		for (const input of inputs) {
			const label = ((input.previousElementSibling || {}).textContent || '').toLowerCase();
			if (label.includes('first')) byLabel.first = input;
			else if (label.includes('last')) byLabel.last = input;
			else if (label.includes('email')) byLabel.email = input;
		}
		// fall back to document order if the labels were not found
		const fields = { first: byLabel.first || inputs[0], last: byLabel.last || inputs[1], email: byLabel.email || inputs[2] };
		for (const key of ['first', 'last', 'email']) {
			fillInput(fields[key], values[key]);
			await sleep(40);
		}

		// group size: the page shows "Total Guests" with - / + buttons (count shown = plus_ones + 1)
		const count = () => parseInt((document.querySelector(SEL.stepperCount) || {}).textContent, 10);
		const buttons = () => document.querySelectorAll(SEL.stepperButton);   // [0] = minus, [1] = plus
		for (let i = 0; i < 4 && count() !== CONFIG.GROUP_SIZE; i++) {
			const button = count() < CONFIG.GROUP_SIZE ? buttons()[1] : buttons()[0];
			if (!button || button.disabled) break;
			button.click();
			await sleep(60);
		}

		// ---- verify before submitting ----
		await sleep(150);
		const problems = [];
		for (const key of ['first', 'last', 'email']) if (fields[key].value !== values[key]) problems.push(`${key} name field shows "${fields[key].value}"`);
		if (count() !== CONFIG.GROUP_SIZE) problems.push(`group size shows ${count()}, wanted ${CONFIG.GROUP_SIZE}`);
		if (problems.length) return banner('Form does not match the configuration, NOT submitting: ' + problems.join('; '), 'error');

		// ---- Step C: submit ----
		if (!CONFIG.SUBMIT) return banner('Form filled in. CONFIG.SUBMIT is false, so click SUBMIT yourself.', 'ok');
		if (sessionStorage.getItem(submittedKey)) return banner(`Already submitted once in this tab for this event. Not sending again (to clear: sessionStorage.removeItem('${submittedKey}')).`, 'warn');
		const submit = document.querySelector(SEL.submitButton);
		if (!submit) return banner('The SUBMIT button was not found.', 'error');
		sessionStorage.setItem(submittedKey, new Date().toISOString());
		await banner('Submitting...');
		clickButton(submit);
		watchRsvpAnswer();
		// The outcome is reported by the fetch recorder above (recordRsvp).
	}

	function pageText() { return (document.body && document.body.innerText) || ''; }
	function looksClosed() { return /registration is now closed|standby booking is now closed|this event is full/i.test(pageText()); }
	function wrongShow(show) {
		const text = pageText();
		if (show === SHOWS.live) return /dress\s+rehearsal/i.test(text);
		return /live\s+show/i.test(text);
	}

	// =====================================================================
	// ===================  PROFILES (settings panel + storage)  ===========
	// =====================================================================
	// A profile is the part of the settings that belongs to a person: the show, who registers, the group size, and whether SUBMIT is clicked.
	// Profiles are kept in localStorage under PROFILES_KEY:
	//   { v: 1, activeId, profiles: [ { id, label, show, firstName, lastName, email, groupSize, submit } ] }
	// localStorage belongs to ONE web address, and this script runs on three of them (snlstandby.nbcuni.com, then pro.vow.app inside its iframe,
	// then go.vow.app in the same iframe), so the TOP page, the one you look at, owns the profiles and shows the settings panel. The copies of the
	// script inside the iframe ask the top page for the active profile with postMessage, and only believe an answer from that page's web address
	// (https://snlstandby.nbcuni.com, or localhost when testing). A page opened on its own has no top page to ask: it uses its own localStorage.
	// If nothing is saved (or localStorage is blocked) the built-in test profile, made from the CONFIG values above, is used.
	const PROFILES_KEY = 'snlProfiles';
	const BUILT_IN_PROFILE = Object.freeze({ id: 'built-in', label: 'Test', show: CONFIG.SHOW, firstName: CONFIG.FIRST_NAME, lastName: CONFIG.LAST_NAME,
		email: CONFIG.EMAIL, groupSize: CONFIG.GROUP_SIZE, submit: CONFIG.SUBMIT });   // taken before CONFIG is changed by a profile
	const MSG_REQUEST = 'snl-profile-request';
	const MSG_REPLY = 'snl-profile-reply';
	const MSG_OPEN_TIME = 'snl-open-time';          // localhost testing: the panel's "Set test open time" button -> the list stage in the iframe
	const MSG_OPEN_TIME_ACK = 'snl-open-time-ack';  //   ... and its answer ("the countdown was adjusted")
	let setOpenTime = null;   // set by the list stage while it runs on this page: moves its open time (and so its countdown); returns true when done
	let profileSummary = '';   // shown in the banner, so you can tell which profile the script is running with
	let profileSummaryHtml = '';   // the same, as styled HTML for the banner (every value escaped)

	const newProfileId = () => Math.random().toString(36).slice(2) + Date.now().toString(36);
	const isLocalOrigin = (origin) => isLocal && /^http:\/\/(localhost|127\.0\.0\.1)(:\d+)?$/.test(origin);

	function describeProfile(p) {
		return `${p.label}: ${SHOWS[p.show].label}, ${p.firstName} ${p.lastName} <${p.email}>, group of ${p.groupSize}, ${p.submit ? 'clicks SUBMIT' : 'does NOT click SUBMIT'}`;
	}

	/** A profile as it should be, or null if it is not usable (a stored or received profile is never trusted as it is). */
	function cleanProfile(p) {
		if (!p || typeof p !== 'object') return null;
		const q = { id: String(p.id || ''), label: String(p.label || '').trim(), show: p.show, firstName: String(p.firstName || '').trim(),
			lastName: String(p.lastName || '').trim(), email: String(p.email || '').trim(), groupSize: Number(p.groupSize), submit: p.submit !== false };
		return q.id && q.label && SHOWS[q.show] && [1, 2].includes(q.groupSize) ? q : null;
	}

	function readStore() {
		try {
			const store = JSON.parse(localStorage.getItem(PROFILES_KEY) || 'null');
			if (!store || !Array.isArray(store.profiles)) return null;
			const profiles = store.profiles.map(cleanProfile).filter(Boolean);
			return profiles.length ? { v: 1, activeId: store.activeId, profiles } : null;
		} catch (e) { return null; }   // nothing stored, damaged, or localStorage blocked
	}

	function writeStore(store) {
		try { localStorage.setItem(PROFILES_KEY, JSON.stringify(store)); return true; } catch (e) { return false; }
	}

	/** The stored profiles; if there are none, a first profile made from the built-in test settings is stored. Null when localStorage does not work. */
	function ensureStore() {
		const existing = readStore();
		if (existing) return existing;
		const first = { ...BUILT_IN_PROFILE, id: newProfileId() };
		const store = { v: 1, activeId: first.id, profiles: [first] };
		return writeStore(store) && readStore() ? store : null;
	}

	/** The active profile of THIS page's localStorage, or the built-in test profile. */
	function ownActiveProfile() {
		const store = readStore();
		return (store && (store.profiles.find((p) => p.id === store.activeId) || store.profiles[0])) || { ...BUILT_IN_PROFILE };
	}

	/** Why a profile cannot be saved, or null. otherProfiles = the profiles it must have a different name from. */
	function profileProblem(p, otherProfiles) {
		if (!p.label) return 'Give the profile a name.';
		if (otherProfiles.some((o) => o.label.toLowerCase() === p.label.toLowerCase())) return `Another profile is already called "${p.label}".`;
		if (!p.firstName) return 'Enter a first name.';
		if (!p.lastName) return 'Enter a last name.';
		if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(p.email)) return 'Enter a valid email address.';
		if (![1, 2].includes(p.groupSize)) return 'The group size must be 1 or 2.';
		return null;
	}

	/** Top page only: answers the copies of the script in the iframe with the active profile (to the web address that asked, if it is one of ours). */
	function answerProfileRequests() {
		window.addEventListener('message', (event) => {
			const data = event.data;
			if (!data || data.type !== MSG_REQUEST) return;
			if (event.origin !== 'https://pro.vow.app' && event.origin !== 'https://go.vow.app' && !isLocalOrigin(event.origin)) return;
			event.source.postMessage({ type: MSG_REPLY, id: data.id, profile: ownActiveProfile() }, event.origin);
		});
	}

	/** Frames only: asks the top page for its active profile. Resolves with the profile, or null if no trusted answer came within PROFILE_WAIT_MS. */
	function askTopForProfile() {
		return new Promise((resolve) => {
			const id = newProfileId();
			const onMessage = (event) => {
				const data = event.data;
				if (event.source !== window.top || !data || data.type !== MSG_REPLY || data.id !== id) return;
				if (event.origin !== 'https://snlstandby.nbcuni.com' && !isLocalOrigin(event.origin)) return;   // not the page we expect around us
				finish(cleanProfile(data.profile));
			};
			const ask = () => window.top.postMessage({ type: MSG_REQUEST, id }, '*');   // the request holds nothing private; the ANSWER is addressed to our origin
			let again, timer;
			function finish(profile) { clearInterval(again); clearTimeout(timer); window.removeEventListener('message', onMessage); resolve(profile); }
			window.addEventListener('message', onMessage);
			ask();
			again = setInterval(ask, 400);   // the top page may not be listening yet
			timer = setTimeout(() => finish(null), CONFIG.PROFILE_WAIT_MS);
		});
	}

	/** The profile this run uses, and where it came from. */
	async function loadActiveProfile() {
		if (window.top !== window) {
			const fromTop = await askTopForProfile();
			if (fromTop) return { profile: fromTop, source: 'the page around this frame' };
			log('no profile from the page around this frame; using this page\'s own storage or the built-in test profile');
		}
		const stored = readStore();
		return { profile: ownActiveProfile(), source: stored ? 'localStorage' : 'the built-in test profile' };
	}

	/** Makes the profile's settings the settings of this run. */
	function applyProfile(profile) {
		CONFIG.SHOW = profile.show; CONFIG.FIRST_NAME = profile.firstName; CONFIG.LAST_NAME = profile.lastName;
		CONFIG.EMAIL = profile.email; CONFIG.GROUP_SIZE = profile.groupSize; CONFIG.SUBMIT = profile.submit;
		profileSummary = describeProfile(profile);
		const dot = ' <span style="opacity:.6">&middot;</span> ';
		profileSummaryHtml = chip('Profile:', 'rgba(255,255,255,.25)') + ' <b>' + esc(profile.label) + '</b>' + dot + esc(SHOWS[profile.show].label) + dot +
			esc(profile.firstName) + ' ' + esc(profile.lastName) + ' &lt;' + esc(profile.email) + '&gt;' + dot + 'group of ' + esc(profile.groupSize) + dot +
			(profile.submit ? 'clicks SUBMIT' : chip('does NOT click SUBMIT', '#ffd54f', '#222'));
	}

	/** Localhost: moves the open time of the list stage, whether it runs in this page or in an iframe. Resolves true if one of them adjusted its countdown. */
	function moveOpenTime(openAtMs) {
		const here = setOpenTime ? setOpenTime(openAtMs) : false;   // this page is itself the show list
		return new Promise((resolve) => {
			const onMessage = (event) => {
				const data = event.data;
				if (data && data.type === MSG_OPEN_TIME_ACK && data.openAtMs === openAtMs && isLocalOrigin(event.origin)) finish(true);
			};
			const timer = setTimeout(() => finish(here), 1000);   // no answer: only an adjustment made here counts
			function finish(result) { clearTimeout(timer); window.removeEventListener('message', onMessage); resolve(result || here); }
			window.addEventListener('message', onMessage);
			for (let i = 0; i < window.frames.length; i++) window.frames[i].postMessage({ type: MSG_OPEN_TIME, openAtMs }, '*');   // the request holds only a time
		});
	}

	/** Top page only: the settings panel. A small badge (top right) shows the active profile; click it to add, edit, select, save or delete profiles. */
	async function mountProfileUi() {
		await waitFor(() => document.body, 10000, 50);
		if (!document.body) return;
		const host = document.createElement('div');
		host.id = 'snl-profile-ui';
		host.style.cssText = 'position:fixed;top:8px;right:8px;z-index:2147483646;';
		const root = host.attachShadow({ mode: 'open' });   // its own styles, so the page's CSS cannot touch it and it cannot touch the page
		root.innerHTML = `
			<style>
				:host { all: initial; }
				* { box-sizing: border-box; font: 12px/1.4 sans-serif; }
				#badge { background: #1f4fd8; color: #fff; border: 0; border-radius: 6px; padding: 5px 10px; cursor: pointer; float: right; }
				#badge.dirty { background: #b26a00; }
				#panel { clear: both; margin-top: 4px; width: 320px; background: #fff; color: #222; border: 1px solid #888; border-radius: 6px; padding: 10px; box-shadow: 0 4px 14px rgba(0,0,0,.35); }
				#panel[hidden] { display: none; }
				h4 { margin: 0 0 6px; font-size: 13px; }
				#active { background: #eef3ff; border-radius: 4px; padding: 5px 7px; margin-bottom: 8px; word-break: break-word; }
				label { display: block; margin: 5px 0 0; color: #555; }
				input[type=text], input[type=email], select { width: 100%; padding: 3px 5px; margin-top: 2px; border: 1px solid #aaa; border-radius: 3px; background: #fff; color: #222; }
				.check { color: #222; } .check input { margin-right: 6px; }
				.row { display: flex; gap: 6px; margin-top: 8px; align-items: center; }
				button.act { padding: 3px 9px; border: 1px solid #888; border-radius: 3px; background: #f3f3f3; color: #222; cursor: pointer; }
				button.primary { background: #1f4fd8; color: #fff; border-color: #1f4fd8; }
				hr { border: 0; border-top: 1px solid #ddd; margin: 8px 0 2px; }
				#msg { margin-top: 6px; min-height: 16px; } #msg.ok { color: #1a7f37; } #msg.err { color: #c62828; }
				small { display: block; margin-top: 6px; color: #666; }
			</style>
			<button id="badge" type="button"></button>
			<div id="panel" hidden>
				<h4>SNL Standby profiles</h4>
				<div id="active"></div>
				<label>Profile <select id="sel"></select></label>
				<div class="row"><button class="act" id="new" type="button">New</button><button class="act" id="del" type="button">Delete</button></div>
				<hr>
				<label>Profile name <input type="text" id="label"></label>
				<label>Show <select id="show"><option value="dress">Dress Rehearsal</option><option value="live">Live Show</option></select></label>
				<label>First name <input type="text" id="firstName"></label>
				<label>Last name <input type="text" id="lastName"></label>
				<label>Email <input type="email" id="email"></label>
				<label>Group size (including you) <select id="groupSize"><option value="1">1</option><option value="2">2</option></select></label>
				<label class="check"><input type="checkbox" id="submit">Click SUBMIT automatically</label>
				<div class="row"><button class="act primary" id="save" type="button">Save</button><button class="act" id="reload" type="button">Reload page</button><button class="act" id="close" type="button">Close</button></div>
				<div id="msg"></div>
				<small>The active profile is used the next time the page loads. After changing it, reload the page.</small>
				<hr><div class="row"><button class="act" id="dlLog" type="button" title="Writes the request log so far as a file">Download request log</button></div>
				<small>The request log is saved by itself when the RSVP is accepted, or 2 minutes after the open time.</small>
				${isLocal ? '<hr><div class="row"><button class="act" id="testOpen" type="button" title="Localhost only: sets when the replay server opens the show">Set test open time...</button></div>' : ''}
			</div>`;
		document.body.appendChild(host);

		const $ = (id) => root.getElementById(id);
		const FORM = ['label', 'show', 'firstName', 'lastName', 'email', 'groupSize'];
		let dirty = false;
		const say = (text, kind = '') => { $('msg').textContent = text; $('msg').className = kind; };

		function activeOf(store) { return store.profiles.find((p) => p.id === store.activeId) || store.profiles[0]; }

		function markDirty(value) {
			dirty = value;
			const store = readStore();
			$('badge').classList.toggle('dirty', dirty);
			$('badge').textContent = 'SNL profile: ' + (store ? activeOf(store).label : BUILT_IN_PROFILE.label) + (dirty ? ' (unsaved)' : '');
		}

		/** Fills the panel from storage: the profile list, the active profile's fields, the badge. */
		function render() {
			const store = ensureStore();
			if (!store) {
				$('active').textContent = 'localStorage is blocked in this browser, so profiles cannot be saved. The built-in test profile is used.';
				$('badge').textContent = 'SNL profile: Test (not saved)';
				return;
			}
			const active = activeOf(store);
			$('sel').textContent = '';
			for (const p of store.profiles) {
				const option = document.createElement('option');
				option.value = p.id; option.textContent = p.label; option.selected = p.id === active.id;
				$('sel').appendChild(option);
			}
			for (const key of FORM) $(key).value = String(active[key]);
			$('submit').checked = active.submit;
			$('active').textContent = 'Runs with: ' + describeProfile(active);
			markDirty(false);
		}

		function formProfile(id) {
			return { id, label: $('label').value.trim(), show: $('show').value, firstName: $('firstName').value.trim(), lastName: $('lastName').value.trim(),
				email: $('email').value.trim(), groupSize: Number($('groupSize').value), submit: $('submit').checked };
		}

		$('badge').addEventListener('click', () => { $('panel').hidden = !$('panel').hidden; });
		$('close').addEventListener('click', () => { $('panel').hidden = true; });
		$('reload').addEventListener('click', () => location.reload());
		$('dlLog').addEventListener('click', () => {
			if (!CONFIG.LOG_ENABLED) return say('The request log is switched off (CONFIG.LOG_ENABLED).', 'err');
			if (!collector.entries.size) return say('No requests have been recorded yet.', 'err');
			say(collectorFinalize('downloaded by hand from the settings panel', true) ? `The log so far (${collector.entries.size} entries) was written to your downloads.` : 'The log could not be written.', 'ok');
		});

		// Localhost only (the button exists only then): ask how many seconds, set that open time on the replay server, and move the countdown.
		if (isLocal) $('testOpen').addEventListener('click', async () => {
			const answer = prompt('How many seconds from now should the show open?', '60');
			if (answer === null) return;   // cancelled: nothing happens
			const text = answer.trim();
			if (!/^\d+$/.test(text) || Number(text) < 1 || Number(text) > 86400) return say('Enter a whole number of seconds from 1 to 86400.', 'err');
			const seconds = Number(text);
			try {
				const response = await fetch(`/__replay/open-in/${seconds}`, { cache: 'no-store' });
				const reply = await response.text();
				if (!response.ok) throw new Error(`HTTP ${response.status} ${reply.slice(0, 100)}`);
				const openAtMs = Date.now() + seconds * 1000;
				const adjusted = await moveOpenTime(openAtMs);
				const at = new Date(openAtMs).toLocaleTimeString();
				say(adjusted ? `The show opens in ${seconds} s (at ${at}). The countdown was adjusted.`
					: `The show opens in ${seconds} s (at ${at}). The page is not on the show list, so there is no countdown to adjust: reload the page to start from the list.`, adjusted ? 'ok' : 'err');
			} catch (e) {
				say('Could not set the open time: ' + (e.message || e), 'err');
			}
		});
		for (const key of [...FORM, 'submit']) { $(key).addEventListener('input', () => markDirty(true)); $(key).addEventListener('change', () => markDirty(true)); }

		$('sel').addEventListener('change', () => {   // select
			const store = readStore();
			if (!store) return;
			if (dirty && !confirm('Discard the changes you have not saved?')) { $('sel').value = activeOf(store).id; return; }
			store.activeId = $('sel').value;
			const saved = writeStore(store);
			render();
			say(saved ? 'Active profile changed. Reload the page to run with it.' : 'Could not save (localStorage blocked).', saved ? 'ok' : 'err');
		});

		$('save').addEventListener('click', () => {   // save (edit)
			const store = readStore();
			if (!store) return say('localStorage is blocked, so nothing can be saved.', 'err');
			const active = activeOf(store);
			const edited = formProfile(active.id);
			const problem = profileProblem(edited, store.profiles.filter((p) => p.id !== active.id));
			if (problem) return say(problem, 'err');
			store.profiles = store.profiles.map((p) => p.id === active.id ? edited : p);
			if (!writeStore(store)) return say('Could not save (localStorage blocked).', 'err');
			render();
			say('Saved. Reload the page to run with it.', 'ok');
		});

		$('new').addEventListener('click', () => {   // add: a copy of the shown profile, valid at once, to edit and save
			const store = readStore();
			if (!store) return say('localStorage is blocked, so nothing can be saved.', 'err');
			if (dirty && !confirm('Discard the changes you have not saved?')) return;
			let label = 'New profile', n = 2;
			while (store.profiles.some((p) => p.label.toLowerCase() === label.toLowerCase())) label = 'New profile ' + n++;
			const copy = { ...activeOf(store), id: newProfileId(), label };
			store.profiles.push(copy);
			store.activeId = copy.id;
			if (!writeStore(store)) return say('Could not save (localStorage blocked).', 'err');
			render();
			say('Added a copy of the previous profile. Change it and click Save.', 'ok');
			$('label').focus(); $('label').select();
		});

		$('del').addEventListener('click', () => {   // remove
			const store = readStore();
			if (!store) return;
			if (store.profiles.length <= 1) return say('You need at least one profile.', 'err');
			const active = activeOf(store);
			if (!confirm(`Delete the profile "${active.label}"?`)) return;
			store.profiles = store.profiles.filter((p) => p.id !== active.id);
			store.activeId = store.profiles[0].id;
			if (!writeStore(store)) return say('Could not save (localStorage blocked).', 'err');
			render();
			say(`Deleted "${active.label}". Reload the page to run with the new active profile.`, 'ok');
		});

		render();
	}

	// =====================================================================
	// ==============================  MAIN  ===============================
	// =====================================================================
	(async function main() {
		// On the real sites the host says which page this is. On the replay server they all share one host, so the path says it instead.
		const host = !isLocal ? location.hostname
			: location.pathname === '/' ? 'snlstandby.nbcuni.com'
			: location.pathname.startsWith('/public/nbc') ? 'pro.vow.app'
			: location.pathname.startsWith('/event/') ? 'go.vow.app' : '';
		if (host === 'pro.vow.app' || host === 'go.vow.app') installCapture(host === 'pro.vow.app' ? 'list' : 'registration');   // before the page's own scripts run
		startLogCollector();   // the top page (or a page on its own) collects the log
		if (window.top === window) {
			// The page you look at: it keeps the profiles, answers the copies of the script in its iframe, and shows the settings panel.
			ensureStore();   // the first time, make a first profile from the built-in test settings
			answerProfileRequests();
			mountProfileUi();
		}
		if (host === 'snlstandby.nbcuni.com') {
			// The show list is in a cross-origin iframe this page cannot reach. Tampermonkey runs another copy of this script inside it.
			log('NBC page: the copy of this script inside the iframe (pro.vow.app) does the work.');
			return;
		}
		const { profile, source } = await loadActiveProfile();
		applyProfile(profile);
		log(`profile: ${profileSummary} (from ${source})`);
		const problem = configProblem();
		if (problem) return banner('Not running: ' + problem, 'error');

		if (host === 'pro.vow.app') await listStage();
		else if (host === 'go.vow.app') await registerStage();
	})().catch((e) => banner('Error: ' + (e && e.message ? e.message : e), 'error'));
})();
