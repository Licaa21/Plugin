using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class DefaultHiddenLibrariesTests
{
    private static readonly Guid Books = Guid.NewGuid();
    private static readonly Guid Music = Guid.NewGuid();
    private static readonly Guid Kids = Guid.NewGuid();

    [Fact]
    public void MissingLibrariesAreAddedAfterTheUsersOwn()
    {
        Assert.Equal(new[] { Kids, Books, Music }, DefaultHiddenLibraries.Merge(new[] { Kids }, new[] { Books, Music }));
    }

    [Fact]
    public void AListThatAlreadyHasThemIsLeftAlone()
    {
        Assert.Null(DefaultHiddenLibraries.Merge(new[] { Music, Books }, new[] { Books, Music }));
    }

    [Fact]
    public void AnEmptyListGetsTheLibraries()
    {
        Assert.Equal(new[] { Books }, DefaultHiddenLibraries.Merge(null, new[] { Books }));
    }
}
