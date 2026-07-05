using Automation;

namespace Snl;

public class SnlTicketSession {

	readonly IAuthHttpClient _http;

	public SnlTicketSession(IAuthHttpClient http) {
		_http = http;
	}

	public async Task GetTicketsAsync(UserInfo user, int groupSize) {
		// !!! validate phone # is in correct format

		var ctx = new Context {
			Http = _http,
			SeriesId = "B9KIOO7ZIQF",
			User = user,
			GroupSize = groupSize
		};

		// ---- Index Page ( also got 500s ) ----
		await Steps.GetIndexPageAsync(ctx);	// snl_may_14(11)

			// throw-away (scripts)
			foreach (string scriptUri in ctx.JavascriptScripts)
				await Steps.RequestScriptAsync(ctx, scriptUri);	// snl_may_14(16, 17)
			ctx.JavascriptScripts.Clear();

			// throw-away (html templates)
			await Steps.RequestTemplateAsync(ctx, Templates.BookingEventWidget);	// snl_may_14(23)
			await Steps.RequestTemplateAsync(ctx, Templates.Footer);				// snl_may_14(24)
			await Steps.RequestTemplateAsync(ctx, Templates.PopupApptSlotExpired);	// snl_may_14(25)
			await Steps.RequestTemplateAsync(ctx, Templates.PopupEventPassed);		// snl_may_14(26)
			await Steps.RequestTemplateAsync(ctx, Templates.PopupMembershipMsg);	// snl_may_14(27)

		// ---- Register Session (needed? - we got 504s) ----
		await Steps.RegisterWidgetSessionAsync(ctx);	// snl_may_14(28)

			// throw-away (event info)
			await Steps.GetSeries_ConfigAsync(ctx);	// snl_may_14(29)
			await Steps.GetSeries_TopicsAsync(ctx);	// snl_may_14(30)
			await Steps.GetSeries_VenuesAsync(ctx);	// snl_may_14(31)

		// ---- GETS LIVE / DRESS show details ----
		await Steps.GetSeriesEventsAsync(ctx);	// snl_may_14(34)
		ctx.SelectedEvent = ctx.Events[0];

			// throw-away
			await Steps.PostSessionAnalyticsEventsAsync(ctx
				, ClickAnalyticsEvent.Null("Select Date", "Event Booking Date")
				, ClickAnalyticsEvent.Null("Select Topics", "Event Booking topics")
				, ClickAnalyticsEvent.Null("Select Store", "Event Booking Store")
			);	// snl_may_14(33)

			// throw-away
			await Steps.RequestTemplateAsync(ctx, Templates.ChooseEvent ); // snl_may_14(35)
			await Steps.RequestTemplateAsync(ctx, Templates.SelectLang ); // snl_may_14(36)
			await Steps.RequestTemplateAsync(ctx, Templates.CookiePolicy ); // snl_may_14(37)
			await Steps.RequestTemplateAsync(ctx, Templates.PrivacyPolicy ); // snl_may_14(38)
			await Steps.RequestTemplateAsync(ctx, Templates.TermConditions ); // snl_may_14(39)

			await Steps.RequestJsonResourceAsync(ctx, StaticJson.MerchPrivacyPolicy ); // snl_may_14(40)
			await Steps.RequestJsonResourceAsync(ctx, StaticJson.MerchCookiePolicy ); // snl_may_14(42)
			await Steps.RequestJsonResourceAsync(ctx, StaticJson.MerchTermsConditions ); // snl_may_14(44)

			await Steps.RequestTemplateAsync(ctx, Templates.DatePicker ); // snl_may_14(41)
			await Steps.RequestTemplateAsync(ctx, Templates.FilterTopics ); // snl_may_14(45)
			await Steps.RequestTemplateAsync(ctx, Templates.Stores ); // snl_may_14(46)
			await Steps.RequestTemplateAsync(ctx, Templates.ShareButton ); // snl_may_14(47)
			await Steps.RequestTemplateAsync(ctx, Templates.EventThumbnail ); // snl_may_14(48)

			// throw-away (language-text)
			await Steps.GetSeriesLanguagesAsync(ctx);	// snl_may_14(49)
			await Steps.GetSeriesTranslationAsync(ctx);// snl_may_14(51)

			// throw-away (html_templates)
			await Steps.RequestTemplateAsync(ctx, "https://bookings-us.qudini.com/eventsBooking/components/event-details/event-details.html");	// snl_may_14(53)

		// ---- Create booking session ----
		await Steps.CreateEventBookingSessionAsync(ctx);	// snl_may_14(54)

			// throw-away
			await Steps.PostSessionAnalyticsEventsAsync(ctx
				, Steps.BuildItemEventThumbnailSelectedEvent(ctx)
			);	// snl_may_14(55)
			await Steps.PostSessionAnalyticsEventsAsync(ctx
				, Steps.BuildItemEventThumbnailSelectedEvent(ctx)
				, ClickAnalyticsEvent.Click("Select Event Thumbnail", "Event Booking: click/select thumbnail event")
			);// snl_may_14(56)
			await Steps.RequestTemplateAsync(ctx, Templates.GroupSize );	// snl_may_14(57)
			await Steps.RequestTemplateAsync(ctx, Templates.CustomerDetails );	// snl_may_14(59)

			// throw-away (analytics)
			await Steps.PostSessionAnalyticsEventsAsync(ctx
				, ClickAnalyticsEvent.Click("Book Event Button Event Details", "Event Booking: book event button")
			);	// snl_may_14(60)
			await Steps.PostSessionAnalyticsEventsAsync(ctx
				, ClickAnalyticsEvent.Click("Book Event Button Event Details", "Event Booking: book event button")
				, ClickAnalyticsEvent.Click("firstName", "First Name")
				, ClickAnalyticsEvent.Click("lastName", "Last Name")
				, ClickAnalyticsEvent.Click("email", "Email")
				, ClickAnalyticsEvent.Click("mobileNumber", "Phone number")
				, ClickAnalyticsEvent.Click("groupSize", "Group Size")
			);// snl_may_14(61)

		// ---- Create Booking (we got a 400 when we ommitted mobile Number) ----
		await Steps.CreateBookingAsync(ctx);	// snl_may_14(64)
		BookingReferenceNumber = ctx.BookingReferenceNumber;

			// throw-away (analytics)
			await Steps.PostSessionAnalyticsEventsAsync(ctx, ClickAnalyticsEvent.Click("Complete Button Customer Details", "Event Booking: customer details complete button"));	// snl_may_14(65)
	}

	public string? BookingReferenceNumber { get; private set; }

}
