using System.Text.Json.Serialization;

namespace Snl;

// Matches the JSON shape expected by the session analytics endpoint (see PostSessionAnalyticsEventsAsync).
// Property names are lowercase to match the wire format exactly, without needing JsonPropertyName attributes.
internal record ClickAnalyticsEvent(string action, ClickAnalyticsEventProperties properties);

// eventType is nullable/omitted because the captured "Select Date"/"Select Topics"/"Select Store"
// events (SubmitUiInteractionEventsAsync) don't include it, unlike every other analytics event.
internal record ClickAnalyticsEventProperties(
	string label,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? eventType,
	string category
);
