using Automation;

namespace Snl;

public class TicketSession {

	Context _context = new();

	public TicketSession(IAuthHttpClient http) {
		_context.Http = http;
	}

	public async Task GetIceCreamTicketsAsync(UserInfo user, int groupSize) {
		_context.SeriesId = "UZJLSRJUNZC";

		// Attendee details captured in snl1 - placeholder/test values, not a real attendee.
		_context.FirstName = user.FirstName;
		_context.LastName = user.LastName;
		_context.Email = user.Email;
		_context.MobileNumber = user.MobileNumber;
		_context.GroupSize = groupSize;

		// snl3(2), snl1(1)
		await Steps.GetIndexPageAsync(_context);

		// snl3(12, 13)
		foreach (string scriptUri in _context.JavascriptScripts)
			await Steps.____RequestGenericAsync(_context, scriptUri);
		_context.JavascriptScripts.Clear();

		// snl3(15)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/view/bookingEventWidget.html");
		// snl3(17)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/footer/q-footer.html");
		// snl3(18)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-appointment-slot-expired.html");
		// snl3(19)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-event-has-passed.html");
		// snl3(20)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-membership-message.html");
		// snl3(21), snl1(2)
		await Steps.GetSeriesConfigAsync(_context);
		// snl3(22), snl1(5) (500 Internal Server Error in both snl1 and snl2)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/booking-widget/event/eventId/choose?timezone=America%2FNew_York");

		// snl3(23), snl1(7)
		await Steps.GetSeriesEventsAsync(_context);

		// snl3(24), snl1(8)
		await Steps.PostSessionAnalyticsEventsAsync(_context
			, new ClickAnalyticsEvent("Select Date", new ClickAnalyticsEventProperties("Event Booking Date: undefined", null, "Event"))
			, new ClickAnalyticsEvent("Select Topics", new ClickAnalyticsEventProperties("Event Booking topics: undefined", null, "Event"))
			, new ClickAnalyticsEvent("Select Store", new ClickAnalyticsEventProperties("Event Booking Store: undefined", null, "Event"))
		);

		// snl3(26)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/choose-event.html");
		// snl3(27)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/select-language/select-language.html");
		// snl3(28)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/cookie-policy/cookie-policy.html");
		// snl3(29)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/privacy-policy/privacy-policy.html");
		// snl3(30)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/terms-conditions/terms-conditions.html");
		// snl3(32)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/datepicker/datepicker.html");
		// snl3(33)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/social-share-buttons/social-share-buttons.html");
		// snl3(34)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/filter-topics/filter-topics.html");
		// snl3(35)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/other-stores/stores.html");
		// snl3(36)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/event-thumbnail.html");

		// snl3(37), snl1(9)
		await Steps.GetSeriesLanguagesAsync(_context);
		// snl3(40)
		await Steps.GetSeriesTranslationAsync(_context);

		// snl3(41), snl1(11)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/event-details/event-details.html");
		// snl3(42), snl1(12)
		await Steps.CreateEventBookingSessionAsync(_context);

		// snl3(43), snl1(13)
		await Steps.PostSessionAnalyticsEventsAsync(_context, Steps.BuildItemEventThumbnailSelectedEvent(_context));
		// snl3(44), snl1(14)
		await Steps.PostSessionAnalyticsEventsAsync(_context
			, Steps.BuildItemEventThumbnailSelectedEvent(_context)
			, new ClickAnalyticsEvent("Select Event Thumbnail", new ClickAnalyticsEventProperties("Event Booking: click/select thumbnail event", "click", "Event"))
		);

		// snl3(45), snl1(15)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/group-size/group-size.html");

		// snl1(16)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/customer-details/customer-details.html");
		// snl1(17)
		await Steps.PostSessionAnalyticsEventsAsync(_context, new ClickAnalyticsEvent("Book Event Button Event Details", new ClickAnalyticsEventProperties("Event Booking: book event button", "click", "Event")));

		// snl1(29)
		await Steps.PostSessionAnalyticsEventsAsync(_context, new ClickAnalyticsEvent("firstName", new ClickAnalyticsEventProperties("First Name", "click", "Event")));
		// snl1(30)
		await Steps.PostSessionAnalyticsEventsAsync(_context, new ClickAnalyticsEvent("email", new ClickAnalyticsEventProperties("Email", "click", "Event")));
		// snl1(31)
		await Steps.PostSessionAnalyticsEventsAsync(_context, new ClickAnalyticsEvent("mobileNumber", new ClickAnalyticsEventProperties("Phone number", "click", "Event")));
		// snl1(33)
		await Steps.PostSessionAnalyticsEventsAsync(_context, new ClickAnalyticsEvent("firstName", new ClickAnalyticsEventProperties("First Name", "click", "Event")));

		// snl1(34)
		await Steps.CreateBookingAsync(_context);
		// snl1(36)
		await Steps.PostSessionAnalyticsEventsAsync(_context, new ClickAnalyticsEvent("Complete Button Customer Details", new ClickAnalyticsEventProperties("Event Booking: customer details complete button", "click", "Event")));
		// snl1(37)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/confirmation/confirmation.html");
	}

	public async Task GetSnlTicketsAsync(UserInfo user, int groupSize) {
		_context.SeriesId = "B9KIOO7ZIQF";

		// Attendee details captured in snl1 - placeholder/test values, not a real attendee.
		_context.FirstName = user.FirstName;
		_context.LastName = user.LastName;
		_context.Email = user.Email;
		_context.MobileNumber = user.MobileNumber;
		_context.GroupSize = groupSize;

		// snl_may_14(2)
		await Steps.GetIndexPageAsync(_context);

		// snl_may_14(3, 4)
		foreach (string scriptUri in _context.JavascriptScripts)
			await Steps.____RequestGenericAsync(_context, scriptUri);
		_context.JavascriptScripts.Clear();

		// snl_may_14(6)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/view/bookingEventWidget.html");
		// snl_may_14(8)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/footer/q-footer.html");
		// snl_may_14(9)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-appointment-slot-expired.html");
		// snl_may_14(10)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-event-has-passed.html");
		// snl_may_14(11)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-membership-message.html");
		// snl_may_14(28) — not observed in snl1 or snl3
		await Steps.RegisterWidgetSessionAsync(_context);
		// snl3(21), snl1(2)
		await Steps.GetSeriesConfigAsync(_context);
		// snl3(22), snl1(5) (500 Internal Server Error in both snl1 and snl2)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/booking-widget/event/eventId/choose?timezone=America%2FNew_York");

		// snl3(23), snl1(7)
		await Steps.GetSeriesEventsAsync(_context);

		// snl3(24), snl1(8)
		await Steps.PostSessionAnalyticsEventsAsync(_context
			, new ClickAnalyticsEvent("Select Date", new ClickAnalyticsEventProperties("Event Booking Date: undefined", null, "Event"))
			, new ClickAnalyticsEvent("Select Topics", new ClickAnalyticsEventProperties("Event Booking topics: undefined", null, "Event"))
			, new ClickAnalyticsEvent("Select Store", new ClickAnalyticsEventProperties("Event Booking Store: undefined", null, "Event"))
		);

		// snl3(26)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/choose-event.html");
		// snl3(27)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/select-language/select-language.html");
		// snl3(28)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/cookie-policy/cookie-policy.html");
		// snl3(29)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/privacy-policy/privacy-policy.html");
		// snl3(30)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/terms-conditions/terms-conditions.html");
		// snl3(32)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/datepicker/datepicker.html");
		// snl3(33)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/social-share-buttons/social-share-buttons.html");
		// snl3(34)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/filter-topics/filter-topics.html");
		// snl3(35)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/other-stores/stores.html");
		// snl3(36)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/event-thumbnail.html");

		// snl3(37), snl1(9)
		await Steps.GetSeriesLanguagesAsync(_context);
		// snl3(40)
		await Steps.GetSeriesTranslationAsync(_context);

		// snl3(41), snl1(11)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/event-details/event-details.html");
		// snl3(42), snl1(12)
		await Steps.CreateEventBookingSessionAsync(_context);

		// snl3(43), snl1(13)
		await Steps.PostSessionAnalyticsEventsAsync(_context, Steps.BuildItemEventThumbnailSelectedEvent(_context));
		// snl3(44), snl1(14)
		await Steps.PostSessionAnalyticsEventsAsync(_context
			, Steps.BuildItemEventThumbnailSelectedEvent(_context)
			, new ClickAnalyticsEvent("Select Event Thumbnail", new ClickAnalyticsEventProperties("Event Booking: click/select thumbnail event", "click", "Event"))
		);

		// snl3(45), snl1(15)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/group-size/group-size.html");

		// snl1(16)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/customer-details/customer-details.html");
		// snl1(17)
		await Steps.PostSessionAnalyticsEventsAsync(_context, new ClickAnalyticsEvent("Book Event Button Event Details", new ClickAnalyticsEventProperties("Event Booking: book event button", "click", "Event")));

		// snl1(29)
		await Steps.PostSessionAnalyticsEventsAsync(_context, new ClickAnalyticsEvent("firstName", new ClickAnalyticsEventProperties("First Name", "click", "Event")));
		// snl1(30)
		await Steps.PostSessionAnalyticsEventsAsync(_context, new ClickAnalyticsEvent("email", new ClickAnalyticsEventProperties("Email", "click", "Event")));
		// snl1(31)
		await Steps.PostSessionAnalyticsEventsAsync(_context, new ClickAnalyticsEvent("mobileNumber", new ClickAnalyticsEventProperties("Phone number", "click", "Event")));
		// snl1(33)
		await Steps.PostSessionAnalyticsEventsAsync(_context, new ClickAnalyticsEvent("firstName", new ClickAnalyticsEventProperties("First Name", "click", "Event")));

		// snl1(34)
		await Steps.CreateBookingAsync(_context);
		// snl1(36)
		await Steps.PostSessionAnalyticsEventsAsync(_context, new ClickAnalyticsEvent("Complete Button Customer Details", new ClickAnalyticsEventProperties("Event Booking: customer details complete button", "click", "Event")));
		// snl1(37)
		await Steps.____RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/confirmation/confirmation.html");
	}


	public string? BookingReferenceNumber => _context.BookingReferenceNumber;

}
