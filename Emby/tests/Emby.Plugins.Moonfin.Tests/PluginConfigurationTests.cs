using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// The Seerr URL is pasted in by an admin and then concatenated with API paths, so it has to come
/// out of the config as an absolute http address or not at all.
/// </summary>
public class PluginConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void NormalizeSeerrUrl_AnswersNullForNothingUsable(string? raw)
    {
        Assert.Null(PluginConfiguration.NormalizeSeerrUrl(raw));
    }

    [Theory]
    [InlineData("\"http://seerr:5055\"")]
    [InlineData("'http://seerr:5055'")]
    [InlineData("  http://seerr:5055  ")]
    public void NormalizeSeerrUrl_StripsWhatPastingLeavesBehind(string raw)
    {
        Assert.Equal("http://seerr:5055", PluginConfiguration.NormalizeSeerrUrl(raw));
    }

    [Fact]
    public void NormalizeSeerrUrl_AssumesHttpWhenNoSchemeIsGiven()
    {
        // Without this, Uri would read "seerr" as the scheme in "seerr:5055".
        Assert.Equal("http://seerr:5055", PluginConfiguration.NormalizeSeerrUrl("seerr:5055"));
        Assert.Equal("http://192.168.1.10:5055", PluginConfiguration.NormalizeSeerrUrl("192.168.1.10:5055"));
    }

    [Theory]
    [InlineData("http://seerr:5055/", "http://seerr:5055")]
    [InlineData("http://seerr:5055///", "http://seerr:5055")]
    [InlineData("https://seerr.example.com/", "https://seerr.example.com")]
    public void NormalizeSeerrUrl_TrimsTrailingSlashesSoPathsConcatenateCleanly(string raw, string expected)
    {
        Assert.Equal(expected, PluginConfiguration.NormalizeSeerrUrl(raw));
    }

    [Theory]
    [InlineData("ftp://seerr:5055")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    public void NormalizeSeerrUrl_RejectsASchemeThatIsNotHttp(string raw)
    {
        Assert.Null(PluginConfiguration.NormalizeSeerrUrl(raw));
    }

    [Fact]
    public void NormalizeSeerrUrl_KeepsHttpsAndAPath()
    {
        Assert.Equal("https://example.com/seerr", PluginConfiguration.NormalizeSeerrUrl("https://example.com/seerr"));
    }
}
