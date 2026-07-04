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
	}

}
