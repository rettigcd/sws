namespace Snl;

// Raw JSON response shapes for deserialization only. See EventMetadata for the extracted subset
// actually needed by Steps/TicketSession.

internal record EventDto(
	int Id,
	string Identifier,
	string Title,
	string StartISO,
	int SlotsAvailable,
	int MaxGroupSize,
	bool HasPassed,
	ShopDto Shop
);

internal record ShopDto(
	string StoreName,
	string StoreIdentifier
);

internal record SessionAnalyticsResponseDto(int? Status, string? Message);

internal record CreateBookingResponseDto(string RefNumber);
