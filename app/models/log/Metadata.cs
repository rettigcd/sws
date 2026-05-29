// extra data attached to each Session
internal sealed record Metadata(
	Dictionary<string, string> Flags,
	Dictionary<string, string> Timers
);
