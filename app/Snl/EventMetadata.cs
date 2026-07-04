namespace Snl;

// Core subset of the event JSON returned by GetSeriesEventsAsync. More fields are available in the
// raw response (shop address/geo/phone, topic details, description, image/banner URLs, waitlist
// counts, etc.) but weren't needed on this first pass - add them here if a Steps method needs them.
internal record EventMetadata(
	int Id,
	string Identifier,
	string Title,
	string StartIso,
	int SlotsAvailable,
	int MaxGroupSize,
	bool HasPassed,
	string StoreName,
	string StoreIdentifier
);
