// ==UserScript==
// @name         SNL Standby Auto-Register (vow.app)
// @namespace    https://github.com/rettigcd/sws
// @version      1.0
// @description  Picks the Live Show or the Dress Rehearsal on the NBC SNL Standby page, fills in the vow.app registration form, sets the group size and submits.
// @match        https://snlstandby.nbcuni.com/*
// @match        https://pro.vow.app/public/nbc*
// @match        https://go.vow.app/event/*/journeys/*
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
// The one exception is the list check in stage 1 (API_POLL_MS), a plain GET of the same public list the page loads.
//
// The RSVP response is recorded (console, and localStorage key "snlRsvpLog") because a SUCCESSFUL response has never been captured.

(function () {
	'use strict';

	// =====================================================================
	// ==========================  CONFIGURATION  ==========================
	// =====================================================================
	const CONFIG = {
		SHOW: 'dress',                      // 'dress' = Dress Rehearsal, 'live' = Live Show

		FIRST_NAME: 'CHANGE_ME',            // <- your details. The script refuses to run while any of these is unchanged.
		LAST_NAME: 'CHANGE_ME',
		EMAIL: 'CHANGE_ME@example.com',
		GROUP_SIZE: 2,                      // total people, including you. The site allows 1 or 2.

		SUBMIT: true,                       // false = fill in the form but do NOT click SUBMIT (for practice)

		API_POLL_MS: 2000,                  // stage 1: check the show list every N ms. The page itself only refreshes every 20 s. 0 = rely on the page.
		GIVE_UP_AFTER_MIN: 90,              // stage 1: stop waiting after this many minutes
		STEP_TIMEOUT_MS: 30000,             // stage 2: how long to wait for each page/step to appear

		DEBUG: true,                        // log to the browser console
	};

	// =====================================================================
	// ======================  KNOWN CONSTANTS (site)  =====================
	// =====================================================================
	// Observed on 2026-09-24 (docs/VOW_SNL_FLOW.md). If vow.app is redeployed, the selectors below are what can break.
	const SHOWS = {
		dress: { label: 'Dress Rehearsal', re: /dress/i },
		live: { label: 'Live Show', re: /live/i },
	};
	const LIST_API = 'https://api.vow.app/api/v2/public/by-url/nbc/events';   // what the list page polls; CORS allows origin https://pro.vow.app

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
	/** Small status box in the top-left corner of the frame. kind: info | ok | warn | error */
	async function banner(text, kind = 'info') {
		log(`[${kind}] ${text}`);
		await waitFor(() => document.body, 10000, 50);
		if (!document.body) return;
		if (!bannerEl || !bannerEl.isConnected) {
			bannerEl = document.createElement('div');
			bannerEl.id = 'snl-auto-banner';
			bannerEl.style.cssText = 'position:fixed;top:0;left:0;z-index:2147483647;max-width:90%;padding:6px 10px;font:12px/1.4 sans-serif;color:#fff;border-bottom-right-radius:6px;white-space:pre-wrap;pointer-events:none';
			document.body.appendChild(bannerEl);
		}
		const colors = { info: '#1f4fd8', ok: '#1a7f37', warn: '#b26a00', error: '#c62828' };
		bannerEl.style.background = colors[kind] || colors.info;
		bannerEl.textContent = 'SNL auto: ' + text;
	}

	function configProblem() {
		if (!SHOWS[CONFIG.SHOW]) return `CONFIG.SHOW must be 'dress' or 'live' (got '${CONFIG.SHOW}').`;
		if (!CONFIG.FIRST_NAME.trim() || CONFIG.FIRST_NAME === 'CHANGE_ME') return 'Set CONFIG.FIRST_NAME.';
		if (!CONFIG.LAST_NAME.trim() || CONFIG.LAST_NAME === 'CHANGE_ME') return 'Set CONFIG.LAST_NAME.';
		if (!CONFIG.EMAIL.includes('@') || /CHANGE_ME/.test(CONFIG.EMAIL)) return 'Set CONFIG.EMAIL.';
		if (![1, 2].includes(CONFIG.GROUP_SIZE)) return 'CONFIG.GROUP_SIZE must be 1 or 2.';
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
	function installRsvpRecorder() {
		const originalFetch = window.fetch;
		window.fetch = async function (input, init) {
			const url = typeof input === 'string' ? input : (input && input.url) || String(input);
			const isRsvp = /\/attendees\/rsvp/.test(url);
			let response;
			try {
				response = await originalFetch.apply(this, arguments);
			} catch (error) {
				if (isRsvp) recordRsvp({ time: new Date().toISOString(), url, status: 0, requestBody: init && init.body, responseBody: 'network error: ' + error });
				throw error;
			}
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
	async function listStage() {
		const show = SHOWS[CONFIG.SHOW];
		const deadline = Date.now() + CONFIG.GIVE_UP_AFTER_MIN * 60000;
		let lastApiCheck = 0;
		let goingTo = null;
		await banner(`Waiting for "${show.label}" to open...`);

		function go(url, how) {
			if (goingTo) return;
			goingTo = url;
			banner(`"${show.label}" is open (${how}). Opening the registration page...`, 'ok');
			location.assign(url);
		}

		while (Date.now() < deadline && !goingTo) {
			// (a) the page itself: a card for the chosen show that has a "Register Now" link
			for (const card of document.querySelectorAll(SEL.card)) {
				const title = (card.querySelector(SEL.cardTitle) || {}).textContent || '';
				const link = card.querySelector(SEL.cardRegisterLink);
				if (show.re.test(title) && link && link.href) { go(link.href, 'page'); break; }
			}

			// (b) the same list the page loads, asked directly and more often than the page's own 20 s refresh
			if (!goingTo && CONFIG.API_POLL_MS > 0 && Date.now() - lastApiCheck >= CONFIG.API_POLL_MS) {
				lastApiCheck = Date.now();
				try {
					const response = await fetch(LIST_API, { headers: { Accept: 'application/json' }, credentials: 'omit' });
					const events = (await response.json()).events || [];
					const event = events.find((e) => show.re.test(e.name || ''));
					if (!event) await banner(`Waiting... no event named like "${show.label}" in the list yet.`);
					else if (event.status === 'open' && event.register_url) go(event.register_url, 'API');
					else await banner(`Waiting for "${event.name}": status "${event.status}" (${event.attending_count}/${event.capacity}). Last check ${new Date().toLocaleTimeString()}`);
				} catch (e) { log('list check failed', e); }
			}
			await sleep(250);
		}
		if (!goingTo) await banner(`Gave up after ${CONFIG.GIVE_UP_AFTER_MIN} minutes.`, 'warn');
	}

	// =====================================================================
	// ============  STAGE 2: the registration page (go.vow.app)  ==========
	// =====================================================================
	async function registerStage() {
		const show = SHOWS[CONFIG.SHOW];
		const eventUuid = (location.pathname.match(/\/event\/([^/]+)/) || [])[1] || 'unknown';
		const submittedKey = 'snlSubmitted_' + eventUuid;
		installRsvpRecorder();
		await banner('Registration page found. Waiting for the page to load...');

		// ---- Step A: get to the form. The page opens on the landing page; the form is the next step. ----
		const first = await waitFor(() => document.querySelector(SEL.landingButton) || document.querySelector(SEL.input), CONFIG.STEP_TIMEOUT_MS);
		if (!first) return banner(`Nothing usable appeared within ${CONFIG.STEP_TIMEOUT_MS / 1000} s. The registration may be closed (the page would say so).`, 'error');
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
	// ==============================  MAIN  ===============================
	// =====================================================================
	(async function main() {
		const host = location.hostname;
		if (host === 'snlstandby.nbcuni.com') {
			// The show list is in a cross-origin iframe this page cannot reach. Tampermonkey runs another copy of this script inside it.
			log('NBC page: the copy of this script inside the iframe (pro.vow.app) does the work.');
			return;
		}
		const problem = configProblem();
		if (problem) return banner('Not running: ' + problem, 'error');

		if (host === 'pro.vow.app') await listStage();
		else if (host === 'go.vow.app') await registerStage();
	})().catch((e) => banner('Error: ' + (e && e.message ? e.message : e), 'error'));
})();
