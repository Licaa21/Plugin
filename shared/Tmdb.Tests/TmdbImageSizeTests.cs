using Moonfin.Tmdb;
using Xunit;

namespace Moonfin.Tmdb.Tests;

/// <summary>
/// The Smart TV asks for a smaller picture with size=w780; everyone else keeps the w1280 redirect.
/// </summary>
public sealed class TmdbImageSizeTests
{
    [Theory]
    [InlineData("w300", "w300")]
    [InlineData("w780", "w780")]
    [InlineData("w1280", "w1280")]
    [InlineData("W780", "w780")]
    [InlineData(null, "w1280")]
    [InlineData("", "w1280")]
    [InlineData("original", "w1280")]
    [InlineData("w500", "w1280")]
    [InlineData("w780/../x", "w1280")]
    public void Choose_KeepsListedWidthsAndDefaultsTheRest(string? size, string expected) =>
        Assert.Equal(expected, TmdbImageSize.Choose(size));
}
