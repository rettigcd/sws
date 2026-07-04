using Automation;

namespace Snl;

public class TicketSession {

	Context _context = new();

	public TicketSession(IAuthHttpClient http) {
		_context.Http = http;
	}

	public async Task GetTicketsAsync() {
		_context.SeriesId = "UZJLSRJUNZC";

		// snl3(2), snl1(1)
		await Steps.GetIndexPageAsync(_context);

		// snl3(12, 13)
		foreach (var scriptUri in _context.JavascriptScripts)
			await Steps.RequestGenericAsync(_context, scriptUri);

		_context.JavascriptScripts.Clear();

		// snl3(15)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/view/bookingEventWidget.html");
		// snl3(17)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/footer/q-footer.html");
		// snl3(18)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-appointment-slot-expired.html");
		// snl3(19)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-event-has-passed.html");
		// snl3(20)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-membership-message.html");
		// snl3(21), snl1(2)
		await Steps.GetSeriesConfigAsync(_context);
		// snl3(22), snl1(5) (500 Internal Server Error in both snl1 and snl2)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/booking-widget/event/eventId/choose?timezone=America%2FNew_York");

		// snl3(23), snl1(7)
		await Steps.GetSeriesEventsAsync(_context);

		// snl3(24), snl1(8)
		await Steps.SubmitUiInteractionEventsAsync(_context);

		// snl3(26)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/choose-event.html");
		// snl3(27)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/select-language/select-language.html");
		// snl3(28)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/cookie-policy/cookie-policy.html");
		// snl3(29)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/privacy-policy/privacy-policy.html");
		// snl3(30)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/terms-conditions/terms-conditions.html");
		// snl3(32)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/datepicker/datepicker.html");
		// snl3(33)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/social-share-buttons/social-share-buttons.html");
		// snl3(34)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/filter-topics/filter-topics.html");
		// snl3(35)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/other-stores/stores.html");
		// snl3(36)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/event-thumbnail.html");

		// snl3(37), snl1(9)
		await Steps.GetSeriesLanguagesAsync(_context);
		// snl3(40)
		await Steps.GetSeriesTranslationAsync(_context);

		// snl3(41), snl1(11)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/event-details/event-details.html");
		// snl3(42), snl1(12)
		await Steps.CreateEventBookingSessionAsync(_context);

		// snl3(43), snl1(13)
		await Steps.SubmitEventThumbnailSelectedAnalyticsAsync(_context);
		// snl3(44), snl1(14)
		await Steps.SubmitEventThumbnailClickedAnalyticsAsync(_context);

		// snl3(45), snl1(15)
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/group-size/group-size.html");
	}

}
