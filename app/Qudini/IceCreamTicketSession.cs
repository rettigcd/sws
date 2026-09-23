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
			User = user,  // Attendee details captured in icecream1 - placeholder/test values, not a real attendee.
			GroupSize = groupSize
		};

		// ---- Index Page ----
		await Steps.GetIndexPageAsync(ctx);	// icecream2(2), icecream1(1)


			foreach (string scriptUri in ctx.JavascriptScripts)
				await Steps.RequestScriptAsync(ctx, scriptUri);
			ctx.JavascriptScripts.Clear();	// icecream2(12, 13)

			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/view/bookingEventWidget.html");		// icecream2(15)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/shared/footer/q-footer.html");		// icecream2(17)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-appointment-slot-expired.html");	// icecream2(18)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-event-has-passed.html");	// icecream2(19)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/popup/popup-membership-message.html");	// icecream2(20)
			await Steps.GetSeries_ConfigAsync(ctx);	// icecream2(21), icecream1(2)
			await Steps.RequestJsonResourceAsync(ctx, "https://bookings-us.qudini.com/booking-widget/event/eventId/choose?timezone=America%2FNew_York");	// icecream2(22), icecream1(5) (500 Internal Server Error in both icecream1 and snl2)

		// ---- Lists the Events that are available for the Series ----
		await Steps.GetSeriesEventsAsync(ctx);	// // icecream2(23), icecream1(7)
		ctx.SelectedEvent = ctx.Events[0];

			await Steps.PostSessionAnalyticsEventsAsync(ctx
				, ClickAnalyticsEvent.Null("Select Date", "Event Booking Date")
				, ClickAnalyticsEvent.Null("Select Topics", "Event Booking topics")
				, ClickAnalyticsEvent.Null("Select Store", "Event Booking Store")
			);	// icecream2(24), icecream1(8)

			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/choose-event.html");// icecream2(26)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/select-language/select-language.html");// icecream2(27)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/shared/cookie-policy/cookie-policy.html");// icecream2(28)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/shared/privacy-policy/privacy-policy.html");// icecream2(29)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/shared/terms-conditions/terms-conditions.html");// icecream2(30)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/datepicker/datepicker.html");// icecream2(32)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/social-share-buttons/social-share-buttons.html");// icecream2(33)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/filter-topics/filter-topics.html");// icecream2(34)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/other-stores/stores.html");// icecream2(35)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/choose-event/event-thumbnail.html");// icecream2(36)

			await Steps.GetSeriesLanguagesAsync(ctx);// icecream2(37), icecream1(9)
			await Steps.GetSeriesTranslationAsync(ctx);// icecream2(40)

			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/event-details/event-details.html");// icecream2(41), icecream1(11)

		// ---- Create Booking Session ----
		await Steps.CreateEventBookingSessionAsync(ctx);// icecream2(42), icecream1(12)

			await Steps.PostSessionAnalyticsEventsAsync(ctx
				, Steps.BuildItemEventThumbnailSelectedEvent(ctx)
			);// icecream2(43), icecream1(13)

			await Steps.PostSessionAnalyticsEventsAsync(ctx
				, Steps.BuildItemEventThumbnailSelectedEvent(ctx)
				, ClickAnalyticsEvent.Click("Select Event Thumbnail", "Event Booking: click/select thumbnail event")
			);// icecream2(44), icecream1(14)

				await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/group-size/group-size.html");// icecream2(45), icecream1(15)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/customer-details/customer-details.html");// icecream1(16)

			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("Book Event Button Event Details", "Event Booking: book event button"));// icecream1(17)

			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("firstName", "First Name"));	// icecream1(29)
			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("email", "Email"));	// icecream1(30)
			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("mobileNumber", "Phone number"));	// icecream1(31)
			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("firstName", "First Name"));	// icecream1(33)

		// ---- Create Booking ----
		await Steps.CreateBookingAsync(ctx);		// icecream1(34)
		BookingReferenceNumber = ctx.BookingReferenceNumber;

			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("Complete Button Customer Details", "Event Booking: customer details complete button"));// icecream1(36)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/confirmation/confirmation.html");	// icecream1(37)
	}

	public string? BookingReferenceNumber { get; private set; }

}
