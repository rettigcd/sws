using Automation;

namespace Snl;

public class TicketSession {

	Context _context = new();

	public TicketSession(IAuthHttpClient http) {
		_context.Http = http;
	}

	public async Task GetTicketsAsync() {
		_context.SeriesId = "UZJLSRJUNZC";

		// snl3 - SessionId: 2
		await Steps.GetIndexPageAsync(_context);

		// snl3 - SessionId: 12, 13
		foreach (var scriptUri in _context.JavascriptScripts)
			await Steps.RequestGenericAsync(_context, scriptUri);

		_context.JavascriptScripts.Clear();

		// snl3 - SessionId: 15
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/view/bookingEventWidget.html");
		// snl3 - SessionId: 17
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/footer/q-footer.html");
		// snl3 - SessionId: 18
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-appointment-slot-expired.html");
		// snl3 - SessionId: 19
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-event-has-passed.html");
		// snl3 - SessionId: 20
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-membership-message.html");
		// snl3 - SessionId: 21
		await Steps.GetSeriesConfigAsync(_context);
		// snl3 - SessionId: 22
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/booking-widget/event/eventId/choose?timezone=America%2FNew_York");

		// snl3 - SessionId: 23
		await Steps.GetSeriesEventsAsync(_context);

		// snl3 - SessionId: 24
		await Steps.SubmitUiInteractionEventsAsync(_context);

		// snl3 - SessionId: 26
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/choose-event.html");
		// snl3 - SessionId: 27
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/select-language/select-language.html");
		// snl3 - SessionId: 28
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/cookie-policy/cookie-policy.html");
		// snl3 - SessionId: 29
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/privacy-policy/privacy-policy.html");
		// snl3 - SessionId: 30
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/terms-conditions/terms-conditions.html");
		// snl3 - SessionId: 32
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/datepicker/datepicker.html");
		// snl3 - SessionId: 33
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/social-share-buttons/social-share-buttons.html");
		// snl3 - SessionId: 34
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/filter-topics/filter-topics.html");
		// snl3 - SessionId: 35
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/other-stores/stores.html");
		// snl3 - SessionId: 36
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/event-thumbnail.html");

		// snl3 - SessionId: 37
		await Steps.GetSeriesLanguagesAsync(_context);
		// snl3 - SessionId: 40
		await Steps.GetSeriesTranslationAsync(_context);

		// snl3 - SessionId: 41
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/event-details/event-details.html");
		// snl3 - SessionId: 42
		await Steps.CreateEventBookingSessionAsync(_context);

		// snl3 - SessionId: 43
		await Steps.SubmitEventThumbnailSelectedAnalyticsAsync(_context);
		// snl3 - SessionId: 44
		await Steps.SubmitEventThumbnailClickedAnalyticsAsync(_context);

		// snl3 - SessionId: 45
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/group-size/group-size.html");
	}

}
