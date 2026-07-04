using Automation;

namespace Snl;

public class TicketSession {

	Context _context = new();

	public TicketSession(IAuthHttpClient http) {
		_context.Http = http;
	}

	public async Task GetTicketsAsync() {
		await Steps.GetIndexPageAsync(_context);

		foreach (var scriptUri in _context.JavascriptScripts)
			await Steps.RequestGenericAsync(_context, scriptUri);

		_context.JavascriptScripts.Clear();

		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/view/bookingEventWidget.html");
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/footer/q-footer.html");
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-appointment-slot-expired.html");
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-event-has-passed.html");
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-membership-message.html");
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/booking-widget/event/series/UZJLSRJUNZC");
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/booking-widget/event/eventId/choose?timezone=America%2FNew_York");
		await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/booking-widget/event/events/UZJLSRJUNZC");
	}

}
