namespace Snl;

public enum StepResult {
	None, // default
	IndexPageRetrieved,
	ScriptRequested,
	UiInteractionEventsSubmitted,
	SessionNotFound,
	ConfigRetrieved,
	EventsRetrieved,
	LanguageOptionsRetrieved,
	LanguageDictRetrieved,
	EventBookingSessionCreated,
	EventThumbnailSelectedAnalyticsSubmitted,
	EventThumbnailClickedAnalyticsSubmitted,
}
