namespace Saz;

internal readonly record struct StatusLineParts(
	int Code,
	string ReasonPhrase
);
