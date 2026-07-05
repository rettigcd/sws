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

		// ---- Index Page ----
		await Steps.GetIndexPageAsync(_context);	// snl3(2), snl1(1)

		
			foreach (string scriptUri in _context.JavascriptScripts)
				await Steps.RequestGenericAsync(_context, scriptUri);
			_context.JavascriptScripts.Clear();	// snl3(12, 13)

			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/view/bookingEventWidget.html");		// snl3(15)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/footer/q-footer.html");		// snl3(17)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-appointment-slot-expired.html");	// snl3(18)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-event-has-passed.html");	// snl3(19)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-membership-message.html");	// snl3(20)
			await Steps.GetSeries_ConfigAsync(_context);	// snl3(21), snl1(2)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/booking-widget/event/eventId/choose?timezone=America%2FNew_York");	// snl3(22), snl1(5) (500 Internal Server Error in both snl1 and snl2)

		// ---- Lists the Events that are available for the Series ----
		await Steps.GetSeriesEventsAsync(_context);	// // snl3(23), snl1(7)
		_context.SelectedEvent = _context.Events[0];

			await Steps.PostSessionAnalyticsEventsAsync(_context
				, ClickAnalyticsEvent.Null("Select Date", "Event Booking Date")
				, ClickAnalyticsEvent.Null("Select Topics", "Event Booking topics")
				, ClickAnalyticsEvent.Null("Select Store", "Event Booking Store")
			);	// snl3(24), snl1(8)

			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/choose-event.html");// snl3(26)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/select-language/select-language.html");// snl3(27)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/cookie-policy/cookie-policy.html");// snl3(28)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/privacy-policy/privacy-policy.html");// snl3(29)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/shared/terms-conditions/terms-conditions.html");// snl3(30)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/datepicker/datepicker.html");// snl3(32)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/social-share-buttons/social-share-buttons.html");// snl3(33)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/filter-topics/filter-topics.html");// snl3(34)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/other-stores/stores.html");// snl3(35)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/event-thumbnail.html");// snl3(36)
			
			await Steps.GetSeriesLanguagesAsync(_context);// snl3(37), snl1(9)
			await Steps.GetSeriesTranslationAsync(_context);// snl3(40)

			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/event-details/event-details.html");// snl3(41), snl1(11)
		
		// ---- Create Booking Session ----
		await Steps.CreateEventBookingSessionAsync(_context);// snl3(42), snl1(12)

			await Steps.PostSessionAnalyticsEventsAsync(_context, Steps.BuildItemEventThumbnailSelectedEvent(_context));// snl3(43), snl1(13)
		
			await Steps.PostSessionAnalyticsEventsAsync(_context
				, Steps.BuildItemEventThumbnailSelectedEvent(_context)
				, ClickAnalyticsEvent.Click("Select Event Thumbnail", "Event Booking: click/select thumbnail event")
			);// snl3(44), snl1(14)

				await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/group-size/group-size.html");// snl3(45), snl1(15)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/customer-details/customer-details.html");// snl1(16)
		
			await Steps.PostSessionAnalyticsEventsAsync(_context, ClickAnalyticsEvent.Click("Book Event Button Event Details", "Event Booking: book event button"));// snl1(17)

			await Steps.PostSessionAnalyticsEventsAsync(_context, ClickAnalyticsEvent.Click("firstName", "First Name"));	// snl1(29)
			await Steps.PostSessionAnalyticsEventsAsync(_context, ClickAnalyticsEvent.Click("email", "Email"));	// snl1(30)
			await Steps.PostSessionAnalyticsEventsAsync(_context, ClickAnalyticsEvent.Click("mobileNumber", "Phone number"));	// snl1(31)
			await Steps.PostSessionAnalyticsEventsAsync(_context, ClickAnalyticsEvent.Click("firstName", "First Name"));	// snl1(33)

		// ---- Create Booking ----
		await Steps.CreateBookingAsync(_context);		// snl1(34)

			await Steps.PostSessionAnalyticsEventsAsync(_context, ClickAnalyticsEvent.Click("Complete Button Customer Details", "Event Booking: customer details complete button"));// snl1(36)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/confirmation/confirmation.html");	// snl1(37)
	}

	public async Task GetSnlTicketsAsync(UserInfo user, int groupSize) {
		_context.SeriesId = "B9KIOO7ZIQF";

		_context.FirstName = user.FirstName;
		_context.LastName = user.LastName;
		_context.Email = user.Email;
		_context.MobileNumber = user.MobileNumber;
		_context.GroupSize = groupSize;

		// ---- Index Page ( also got 500s ) ----
		await Steps.GetIndexPageAsync(_context);	// snl_may_14(11)

			// throw-away (scripts)
			foreach (string scriptUri in _context.JavascriptScripts)
				await Steps.RequestGenericAsync(_context, scriptUri);	// snl_may_14(16, 17)
			_context.JavascriptScripts.Clear();

			// throw-away (html templates)
			await Steps.RequestGenericAsync(_context, Templates.BookingEventWidget);	// snl_may_14(23)
			await Steps.RequestGenericAsync(_context, Templates.Footer);				// snl_may_14(24)
			await Steps.RequestGenericAsync(_context, Templates.PopupApptSlotExpired);	// snl_may_14(25)
			await Steps.RequestGenericAsync(_context, Templates.PopupEventPassed);		// snl_may_14(26)
			await Steps.RequestGenericAsync(_context, Templates.PopupMembershipMsg);	// snl_may_14(27)

		// ---- Register Session (needed? - we got 504s) ----
		await Steps.RegisterWidgetSessionAsync(_context);	// snl_may_14(28)

			// throw-away (event info)
			await Steps.GetSeries_ConfigAsync(_context);	// snl_may_14(29)
			await Steps.GetSeries_TopicsAsync(_context);	// snl_may_14(30)
			await Steps.GetSeries_VenuesAsync(_context);	// snl_may_14(31)

		// ---- GETS LIVE / DRESS show details ----
		await Steps.GetSeriesEventsAsync(_context);	// snl_may_14(34)
		_context.SelectedEvent = _context.Events[0];

			// throw-away
			await Steps.PostSessionAnalyticsEventsAsync(_context
				, ClickAnalyticsEvent.Null("Select Date", "Event Booking Date")
				, ClickAnalyticsEvent.Null("Select Topics", "Event Booking topics")
				, ClickAnalyticsEvent.Null("Select Store", "Event Booking Store")
			);	// snl_may_14(33)

			// throw-away
			await Steps.RequestGenericAsync(_context, Templates.ChooseEvent ); // snl_may_14(35)
			await Steps.RequestGenericAsync(_context, Templates.SelectLang ); // snl_may_14(36)
			await Steps.RequestGenericAsync(_context, Templates.CookiePolicy ); // snl_may_14(37)
			await Steps.RequestGenericAsync(_context, Templates.PrivacyPolicy ); // snl_may_14(38)
			await Steps.RequestGenericAsync(_context, Templates.TermConditions ); // snl_may_14(39)

			await Steps.RequestGenericAsync(_context, StaticJson.MerchPrivacyPolicy ); // snl_may_14(40)
			await Steps.RequestGenericAsync(_context, StaticJson.MerchCookiePolicy ); // snl_may_14(42)
			await Steps.RequestGenericAsync(_context, StaticJson.MerchTermsConditions ); // snl_may_14(44)

			await Steps.RequestGenericAsync(_context, Templates.DatePicker ); // snl_may_14(41)
			await Steps.RequestGenericAsync(_context, Templates.FilterTopics ); // snl_may_14(45)
			await Steps.RequestGenericAsync(_context, Templates.Stores ); // snl_may_14(46)
			await Steps.RequestGenericAsync(_context, Templates.ShareButton ); // snl_may_14(47)
			await Steps.RequestGenericAsync(_context, Templates.EventThumbnail ); // snl_may_14(48)

			// throw-away (language-text)
			await Steps.GetSeriesLanguagesAsync(_context);	// snl_may_14(49)
			await Steps.GetSeriesTranslationAsync(_context);// snl_may_14(51)

			// throw-away (html_templates)
			await Steps.RequestGenericAsync(_context, "https://bookings-us.qudini.com/eventsBooking/components/event-details/event-details.html");	// snl_may_14(53)

		// ---- Create booking session ----
		await Steps.CreateEventBookingSessionAsync(_context);	// snl_may_14(54)

			// Throw-away (analytics)		
			await Steps.PostSessionAnalyticsEventsAsync(_context, Steps.BuildItemEventThumbnailSelectedEvent(_context));	// snl_may_14(55)
			await Steps.PostSessionAnalyticsEventsAsync(_context
				, Steps.BuildItemEventThumbnailSelectedEvent(_context)
				, ClickAnalyticsEvent.Click("Select Event Thumbnail", "Event Booking: click/select thumbnail event")
			);// snl_may_14(56)

			// throw-away (html-templates)
			await Steps.RequestGenericAsync(_context, Templates.GroupSize );	// snl_may_14(57)
			await Steps.RequestGenericAsync(_context, Templates.CustomerDetails );	// snl_may_14(59)

			// throw-away (analytics)		
			await Steps.PostSessionAnalyticsEventsAsync(_context, ClickAnalyticsEvent.Click("Book Event Button Event Details", "Event Booking: book event button"));	// snl_may_14(60)
			await Steps.PostSessionAnalyticsEventsAsync(_context
				, ClickAnalyticsEvent.Click("Book Event Button Event Details", "Event Booking: book event button")
				, ClickAnalyticsEvent.Click("firstName", "First Name")
				, ClickAnalyticsEvent.Click("lastName", "Last Name")
				, ClickAnalyticsEvent.Click("email", "Email")
				, ClickAnalyticsEvent.Click("mobileNumber", "Phone number")
				, ClickAnalyticsEvent.Click("groupSize", "Group Size")
			);// snl_may_14(61)

		// ---- Create Booking (we got a 400) ----
		await Steps.CreateBookingAsync(_context);	// snl_may_14(64)
		
			// throw-away (analytics)		
			await Steps.PostSessionAnalyticsEventsAsync(_context, ClickAnalyticsEvent.Click("Complete Button Customer Details", "Event Booking: customer details complete button"));	// snl_may_14(65)
	}


	public string? BookingReferenceNumber => _context.BookingReferenceNumber;

}