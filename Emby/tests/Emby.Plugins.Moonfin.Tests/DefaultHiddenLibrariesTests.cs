using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

public class DefaultHiddenLibrariesTests
{
    [Fact]
    public void MissingLibrariesAreAddedAfterTheUsersOwn()
    {
        Assert.Equal(new[] { "12", "3", "8" }, DefaultHiddenLibraries.Merge(new[] { "12" }, new[] { "3", "8" }));
    }

    [Fact]
    public void AListThatAlreadyHasThemIsLeftAlone()
    {
        Assert.Null(DefaultHiddenLibraries.Merge(new[] { "8", "3" }, new[] { "3", "8" }));
    }

    [Fact]
    public void AnEmptyListGetsTheLibraries()
    {
        Assert.Equal(new[] { "3" }, DefaultHiddenLibraries.Merge(null, new[] { "3" }));
    }
}
