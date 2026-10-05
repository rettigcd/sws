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

		// The one polling rate, used once the rapid polling has started: modes 0 and 1 fetch the show list themselves every N ms (0 = rely on the page's
		// own 20 s refresh); mode 2 makes the page refresh its own list every N ms (must be at least 100 there).
		FAST_POLL_MS: 1000,
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

		DEBUG: true,                        // log to the browser console

		// ---- SPEED-UP of the list on screen (stage 1) ----
		// The list page only redraws itself every 20 s, so its "Register Now" button can be up to 20 s late. This picks what, if anything, makes
		// it redraw sooner (details in the comment above listStage).
		//   'auto' = the script looks at the page's list component a few seconds after the list page loads (and again at the start of the
		//            rapid polling) and uses the best mode the page allows: 2 if it has load() and its timer, else 1 if it has $set(), else 0.
		//            The countdown banner shows the choice. (default)
		// or force one mode by number:
		//   0 = change nothing: we navigate to the registration page ourselves as soon as our poll sees "open"  (no speed-up)
		//   1 = when OUR poll finds the show open, push the events it got into the page and follow the page's own "Register Now" link; if the
		//       push cannot be done or no link shows within PUSH_WAIT_MS, redirect to register_url ourselves (= mode 0)
		//   2 = leave the page's own 20 s timer alone until RAPID_POLL_LEAD_SECONDS before opening, then cancel it and run a timer
		//       of FAST_POLL_MS instead. The script does NOT fetch the list by itself in this mode: it follows the "Register Now" link the
		//       page draws. If the page's component cannot be found it polls itself; and if no link has shown WATCHDOG_MS after the open
		//       time, it starts polling itself as well.
		// Mode 0 was run against the replay server (headless Chrome); the registration page was requested about 0.3 s after the open.
		SPEEDUP: 'auto',
		CAPABILITY_WAIT_MS: 2500,           // 'auto': how long after the list page starts to wait for the page's list component before concluding it is not there
		PUSH_WAIT_MS: 1000,                 // mode 1: how long to wait for the page's link after pushing the events before redirecting ourselves
		WATCHDOG_MS: 3000,                  // mode 2: if no link has shown this long after the open time, start our own polling too

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
		const full = (profileSummaryHtml ? `<div style="margin-bottom:4px">${profileSummaryHtml}</div>` : '') + `<div>${body}</div>`;
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
		if (!['auto', 0, 1, 2].includes(CONFIG.SPEEDUP)) return "CONFIG.SPEEDUP must be 'auto', 0, 1 or 2.";
		if ((CONFIG.SPEEDUP === 2 || CONFIG.SPEEDUP === 'auto') && !(CONFIG.FAST_POLL_MS >= 100)) return 'CONFIG.FAST_POLL_MS must be at least 100 when speed-up mode 2 can be used.';
		if (!(CONFIG.PROFILE_WAIT_MS >= 0)) return 'CONFIG.PROFILE_WAIT_MS must be 0 or more.';
		if (!(CONFIG.CAPABILITY_WAIT_MS >= 0 && CONFIG.PUSH_WAIT_MS >= 0 && CONFIG.WATCHDOG_MS >= 0)) return 'CONFIG.CAPABILITY_WAIT_MS, PUSH_WAIT_MS and WATCHDOG_MS must be 0 or more.';
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

	function recordRsvp(entry) {
		(window.__snlRsvpLog = window.__snlRsvpLog || []).push(entry);
		try { localStorage.setItem('snlRsvpLog', JSON.stringify(window.__snlRsvpLog)); } catch (e) { /* storage blocked */ }
		log('RSVP response', entry);
		if (entry.status >= 200 && entry.status < 300) banner(`RSVP accepted (HTTP ${entry.status}). Check the page for the confirmation number.`, 'ok');
		else if (entry.status === 422 && /capacity_full/.test(entry.responseBody)) banner('The event is FULL (HTTP 422).', 'error');
		else if (entry.status === 429) banner('Rate limited (HTTP 429). Do not keep clicking; wait.', 'error');
		else if (entry.status === 0) banner('The RSVP request did not get an answer (network error). It may or may not have reached the server; check before trying again.', 'error');
		else banner(`RSVP failed: HTTP ${entry.status} ${entry.responseBody.slice(0, 200)}`, 'error');
	}

	// =====================================================================
	// ==================  STAGE 1: the show list (pro.vow.app)  ===========
	// =====================================================================
	// Speeding up the page's own 20 s refresh (the "Register Now" button only appears when the page itself reloads the list).
	// The list page is a Vue 2 component (.nbc-page) with data { events, pollTimer } and a method load({silent}) that GETs LIST_API,
	// sets this.events, and is called by setInterval(..., 2e4). Because @grant none runs us in the page's own JS world, the component
	// is reachable as  document.querySelector('.nbc-page').__vue__  (call it vm). The options, selected with CONFIG.SPEEDUP ('auto' picks the best
	// one the page allows, see bestSpeedup; or force 0, 1 or 2):
	//   0. No change needed: we already navigate to register_url ourselves as soon as our own poll sees "open" (go() below), so the
	//      page's button is never needed. Only the on-screen list stays up to 20 s behind.
	//   1. Feed our finding into the page:   vm.$set(vm, 'events', json.events)  when our poll finds the chosen show open (not on the polls
	//      before that), then follow the page's own "Register Now" link. If $set cannot be done, or no link shows within PUSH_WAIT_MS, we
	//      redirect to register_url ourselves (mode 0). No extra requests.
	//   2. Replace the page's timer:         at the rapid-polling start clearInterval(vm.pollTimer); vm.pollTimer = setInterval(() => vm.load({ silent: true }),
	//      CONFIG.FAST_POLL_MS) -- the page's slow 20 s timer is cancelled and the page polls at the fast rate. Our own poll is switched
	//      off in this mode; we go to the page's "Register Now" link (checked every 250 ms). If the component is not found we poll ourselves.
	// Cautions: wait for .nbc-page to exist (it mounts after we start); .nbc-page / load are the site's internal names and can change
	// with a rebuild, so check they exist. The rate limit of 10 is on the RSVP request only (docs/VOW_SNL_FLOW.md 5.4); the show list returned no
	// limit headers and was not throttled at about 3 requests per second (2,619 requests on 2026-10-01), so extra list fetches do not use it up.
	/** The list page's Vue component (see the comment above), or null if it is not there (yet) or does not have the events list we expect. */
	function pageVm() {
		const el = document.querySelector('.nbc-page');
		const vm = el && el.__vue__;
		return vm && Array.isArray(vm.events) ? vm : null;
	}

	/** What the page's component allows right now: { mode: 2 | 1 | 0, why } (the best speed-up mode, and the reason). */
	function bestSpeedup() {
		const vm = pageVm();
		if (!vm) return { mode: 0, why: 'the page component (.nbc-page) with its events list was not found' };
		if (typeof vm.load === 'function' && 'pollTimer' in vm) return { mode: 2, why: 'the page component has load() and its timer' };
		if (typeof vm.$set === 'function') return { mode: 1, why: 'the page component has events and $set(), but not load() and its timer' };
		return { mode: 0, why: 'the page component has neither load() with its timer nor $set()' };
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

	/** Speed-up mode 2: cancel the page's 20 s timer and replace it with one every CONFIG.FAST_POLL_MS. Resolves true when done, false if the page cannot do it. */
	async function installFastPageTimer() {
		const vm = await waitFor(pageVm, CONFIG.CAPABILITY_WAIT_MS);
		if (!vm || typeof vm.load !== 'function' || !('pollTimer' in vm)) { log('speed-up mode 2: the page component (or its load() and timer) is not there, so its timer was left alone'); return false; }
		clearInterval(vm.pollTimer);
		vm.pollTimer = setInterval(() => vm.load({ silent: true }), CONFIG.FAST_POLL_MS);
		log(`speed-up mode 2: the page now refreshes its list every ${CONFIG.FAST_POLL_MS} ms`);
		return true;
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
			const response = await fetch(`/__replay/open-in/${seconds}`, { cache: 'no-store' });
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
		let lastApiCheck = 0;
		let lastWaitingLog = '';
		let waitingNote = '';   // the latest poll result, shown under the countdown once the rapid polling has started
		let ownPoll = true;   // does the script fetch the list itself? Not in mode 2, which relies on the page's own (sped-up) polling
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
			return true;
		};
		if (isLocal) {
			window.addEventListener('message', (event) => {   // the settings panel in the page around this frame asks for the change
				const data = event.data;
				if (!data || data.type !== MSG_OPEN_TIME || event.source !== window.top || !isLocalOrigin(event.origin)) return;
				if (setOpenTime(Number(data.openAtMs))) event.source.postMessage({ type: MSG_OPEN_TIME_ACK, openAtMs: data.openAtMs }, event.origin);
			});
		}

		function go(url, how) {
			if (goingTo) return;
			goingTo = url;
			banner(`"${show.label}" is open (${how}). Opening the registration page...`, 'ok');
			location.assign(url);
		}

		/** Redraws the status box: the show, the countdown to the open time, the speed-up mode, and what the script is doing. rapid = the rapid polling has
		 *  started. The countdown runs on in the rapid polling phase, until the open time. Screen only (quiet): the console gets lines when things change. */
		async function drawStatus(rapid) {
			if (goingTo) return;   // do not paint over the "is open, opening the registration page" message
			const msLeft = openAt.getTime() - Date.now();
			const opensAt = openAt.toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit' });
			const left = formatCountdown(msLeft);
			const speedupText = speedupDecided ? `mode ${speedup} (${speedupWhy})` : speedupWhy;
			const modeColor = { 2: '#2e7d32', 1: '#f9a825', 0: '#546e7a' }[speedup];
			const reached = msLeft <= 0;
			let doing;   // the last line: what is going on now
			if (!rapid) doing = `Rapid polling starts ${CONFIG.RAPID_POLL_LEAD_SECONDS} s before opening.`;
			else if (reached) doing = `Open time reached. ${waitingNote || 'Waiting for the page to show the link...'}`;
			else doing = `Rapid polling: ${waitingNote || (speedup === 2 ? 'the page refreshes its own list.' : 'asking the list ourselves...')}`;
			await banner(
				`"${show.label}" opens ${opensAt}${testOpenAt ? ' (TEST)' : ''}\nCountdown  ${left}\nSpeed-up: ${speedupText}\n${doing}`,
				'info', true,
				`<div style="font-size:13px"><b>${esc(show.label)}</b> opens ${esc(opensAt)}${testOpenAt ? ' ' + chip('TEST', '#ffd54f', '#222') : ''}</div>` +
				`<div style="margin:3px 0"><span style="opacity:.85">Countdown</span> <span style="font:700 26px/1.15 Consolas,monospace;letter-spacing:1px;background:rgba(0,0,0,.28);border-radius:4px;padding:0 8px">${esc(left)}</span>` +
				`${rapid ? ' ' + chip(reached ? 'OPEN TIME REACHED' : 'RAPID POLLING', reached ? '#2e7d32' : '#b26a00') : ''}</div>` +
				`<div>Speed-up: ${speedupDecided ? chip('mode ' + speedup, modeColor) + ' <span style="opacity:.85">' + esc(speedupWhy) + '</span>' : '<span style="opacity:.85">' + esc(speedupWhy) + '</span>'}</div>` +
				`<div style="opacity:.85">${esc(doing)}</div>`);
		}

		// ---- before the rapid polling: only a countdown on screen (and the cheap look at the page below, which sends nothing) ----
		const stageStart = Date.now();
		let lastCapabilityCheck = 0;
		let lastCountdownLog = 0;
		while (Date.now() < rapidFrom && !goingTo) {
			const link = pageRegisterLink(show);
			if (link) { go(link, 'page'); break; }
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
		if (!goingTo) {
			if (CONFIG.SPEEDUP === 'auto') {   // the latest look at the page decides
				const best = bestSpeedup();
				speedup = best.mode; speedupWhy = 'auto: ' + best.why; speedupDecided = true;
				log(`speed-up auto: mode ${speedup} (${best.why})`);
			}
			log(`Rapid polling started (speed-up mode ${speedup}). Waiting for "${show.label}" to open...`);
			await drawStatus(true);
			ownPoll = speedup !== 2;
			if (speedup === 2) installFastPageTimer().then((ok) => {
				if (!ok) {   // could not take over the page's timer after all: poll ourselves, with whatever the page still allows
					const best = bestSpeedup();
					speedup = best.mode === 2 ? 0 : best.mode; ownPoll = true;
					log(`speed-up mode 2: falling back to our own polling, speed-up mode ${speedup}`);
				}
			});
		}

		while (Date.now() < deadline && !goingTo) {
			// (a) the page itself: a card for the chosen show that has a "Register Now" link
			const link = pageRegisterLink(show);
			if (link) go(link, 'page');

			// mode 2 has no poll of its own: if the page has shown no link WATCHDOG_MS after the open time, start polling ourselves too
			if (speedup === 2 && !ownPoll && !goingTo && Date.now() > openAt.getTime() + CONFIG.WATCHDOG_MS) {
				ownPoll = true;
				log(`speed-up mode 2: no link on the page ${CONFIG.WATCHDOG_MS} ms after the open time; polling ourselves as well`);
			}

			// (b) the same list the page loads, asked directly and more often than the page's own 20 s refresh
			if (!goingTo && ownPoll && CONFIG.FAST_POLL_MS > 0 && Date.now() - lastApiCheck >= CONFIG.FAST_POLL_MS) {
				lastApiCheck = Date.now();
				try {
					const response = await fetch(LIST_API, { headers: { Accept: 'application/json' }, credentials: 'omit' });
					const events = (await response.json()).events || [];
					const event = events.find((e) => show.re.test(e.name || ''));
					if (!event) {
						const waiting = `Waiting... no event named like "${show.label}" in the list yet.`;
						if (waiting !== lastWaitingLog) { lastWaitingLog = waiting; log(waiting); }
						waitingNote = waiting;
					}
					else if (event.status === 'open' && event.register_url) {
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
					}
					else {
						const seats = event.capacity != null ? ` (${event.attending_count}/${event.capacity})` : '';   // the Oct 1 list has no seat counts
						const waiting = `Waiting for "${event.name}": status "${event.status}"${seats}.`;
						if (waiting !== lastWaitingLog) { lastWaitingLog = waiting; log(waiting); }   // the console gets a line only when the status changes
						waitingNote = `${waiting} Last check ${new Date().toLocaleTimeString()}`;
					}
				} catch (e) { log('list check failed', e); }
			}
			if (!goingTo) await drawStatus(true);   // every pass: the countdown keeps running until the open time
			await sleep(250);
		}
		if (!goingTo) await banner(`Gave up after ${CONFIG.GIVE_UP_AFTER_MIN} minutes.`, 'warn');
	}

	// =====================================================================
	// ============  STAGE 2: the registration page (go.vow.app)  ==========
	// =====================================================================
	/** Why the registration page is not going to load, or null. (The page's own journey call failed, or it shows its error screen.) */
	function registrationFailure() {
		if (journeyFailure) return `the journey call failed (${journeyFailure})`;
		if (/no longer available|link has expired/i.test(pageText())) return 'the page says the link is no longer available';
		return null;
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
