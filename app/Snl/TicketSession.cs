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
			await Steps.RequestGenericScriptAsync(_context, scriptUri);

		_context.JavascriptScripts.Clear();

		await Steps.RequestGenericScriptAsync(_context, "https://bookings-us.qudini.com/view/bookingEventWidget.html");
	}

}
