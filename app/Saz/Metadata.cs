namespace Saz;

// extra data attached to each Exchange
internal sealed record Metadata(
	Dictionary<string, string> Flags,
	Dictionary<string, string> Timers
);
