namespace sws.Tests;

using Shouldly;
using Xunit;
using Xunit.Sdk;

public class Interesting_Tests
{
	[Theory]
	[InlineData("eyJpZCI6IjAxOWU2ZjUyLWZhNTMtN2ZhZi04YTUxLTRjNmRlNDJmYzk2OSIsIm1ldGEiOnsiaW50ZXJhY3Rpb25UeXBlIjoicmVkaXJlY3QifX0=")]
	public void Interesting_Words(string word) {
		InterestingFinder.IsInteresting(word).ShouldBeTrue();
	}

	[Theory]
	[InlineData("svcs.tql.com")]
	[InlineData("LeadCrossReference")]
	[InlineData("GetUserVersionInfo")]
	public void NotInteresting_Words(string word) {
		InterestingFinder.IsInteresting(word).ShouldBeFalse(word);
	}
}
