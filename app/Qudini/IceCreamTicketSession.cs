using Automation;

namespace Qudini;

public class IceCreamTicketSession {

	readonly IAuthHttpClient _http;

	public IceCreamTicketSession(IAuthHttpClient http) {
		_http = http;
	}

	public async Task GetTicketsAsync(UserInfo user, int groupSize) {
		var ctx = new Context {
			Http = _http,
			SeriesId = "UZJLSRJUNZC",
			User = user,  // Attendee details captured in snl1 - placeholder/test values, not a real attendee.
			GroupSize = groupSize
		};

		// ---- Index Page ----
		await Steps.GetIndexPageAsync(ctx);	// snl3(2), snl1(1)


			foreach (string scriptUri in ctx.JavascriptScripts)
				await Steps.RequestScriptAsync(ctx, scriptUri);
			ctx.JavascriptScripts.Clear();	// snl3(12, 13)

			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/view/bookingEventWidget.html");		// snl3(15)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/shared/footer/q-footer.html");		// snl3(17)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-appointment-slot-expired.html");	// snl3(18)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-event-has-passed.html");	// snl3(19)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-membership-message.html");	// snl3(20)
			await Steps.GetSeries_ConfigAsync(ctx);	// snl3(21), snl1(2)
			await Steps.RequestJsonResourceAsync(ctx, "https://bookings-us.qudini.com/booking-widget/event/eventId/choose?timezone=America%2FNew_York");	// snl3(22), snl1(5) (500 Internal Server Error in both snl1 and snl2)

		// ---- Lists the Events that are available for the Series ----
		await Steps.GetSeriesEventsAsync(ctx);	// // snl3(23), snl1(7)
		ctx.SelectedEvent = ctx.Events[0];

			await Steps.PostSessionAnalyticsEventsAsync(ctx
				, ClickAnalyticsEvent.Null("Select Date", "Event Booking Date")
				, ClickAnalyticsEvent.Null("Select Topics", "Event Booking topics")
				, ClickAnalyticsEvent.Null("Select Store", "Event Booking Store")
			);	// snl3(24), snl1(8)

			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/choose-event.html");// snl3(26)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/select-language/select-language.html");// snl3(27)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/shared/cookie-policy/cookie-policy.html");// snl3(28)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/shared/privacy-policy/privacy-policy.html");// snl3(29)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/shared/terms-conditions/terms-conditions.html");// snl3(30)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/datepicker/datepicker.html");// snl3(32)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/social-share-buttons/social-share-buttons.html");// snl3(33)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/filter-topics/filter-topics.html");// snl3(34)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/other-stores/stores.html");// snl3(35)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/event-thumbnail.html");// snl3(36)

			await Steps.GetSeriesLanguagesAsync(ctx);// snl3(37), snl1(9)
			await Steps.GetSeriesTranslationAsync(ctx);// snl3(40)

			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/event-details/event-details.html");// snl3(41), snl1(11)

		// ---- Create Booking Session ----
		await Steps.CreateEventBookingSessionAsync(ctx);// snl3(42), snl1(12)

			await Steps.PostSessionAnalyticsEventsAsync(ctx
				, Steps.BuildItemEventThumbnailSelectedEvent(ctx)
			);// snl3(43), snl1(13)

			await Steps.PostSessionAnalyticsEventsAsync(ctx
				, Steps.BuildItemEventThumbnailSelectedEvent(ctx)
				, ClickAnalyticsEvent.Click("Select Event Thumbnail", "Event Booking: click/select thumbnail event")
			);// snl3(44), snl1(14)

				await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/group-size/group-size.html");// snl3(45), snl1(15)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/customer-details/customer-details.html");// snl1(16)

			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("Book Event Button Event Details", "Event Booking: book event button"));// snl1(17)

			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("firstName", "First Name"));	// snl1(29)
			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("email", "Email"));	// snl1(30)
			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("mobileNumber", "Phone number"));	// snl1(31)
			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("firstName", "First Name"));	// snl1(33)

		// ---- Create Booking ----
		await Steps.CreateBookingAsync(ctx);		// snl1(34)
		BookingReferenceNumber = ctx.BookingReferenceNumber;

			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("Complete Button Customer Details", "Event Booking: customer details complete button"));// snl1(36)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/confirmation/confirmation.html");	// snl1(37)
	}

	public string? BookingReferenceNumber { get; private set; }

}
