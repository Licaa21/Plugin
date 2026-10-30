using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// One gb core plays Game Boy and Game Boy Color, but libretro keeps their art in separate sets,
/// so the system folder decides which set is tried first.
/// </summary>
public class GameBoyColorArtworkTests
{
    [Theory]
    [InlineData("GBC", "Nintendo - Game Boy Color", "Nintendo - Game Boy")]
    [InlineData("Game Boy Color", "Nintendo - Game Boy Color", "Nintendo - Game Boy")]
    [InlineData("Game Boy", "Nintendo - Game Boy", "Nintendo - Game Boy Color")]
    public void AGameBoyFolderTriesItsOwnSetBeforeTheOther(string systemName, string first, string second)
    {
        var platforms = GameThumbService.PlatformsFor(GamesScanner.ArtworkCore("gb", systemName));

        Assert.Equal(new[] { first, second }, platforms);
    }

    [Fact]
    public void OtherSystemsKeepTheirOneSet()
    {
        var platforms = GameThumbService.PlatformsFor(GamesScanner.ArtworkCore("gba", "GBA"));

        Assert.Equal(new[] { "Nintendo - Game Boy Advance" }, platforms);
    }
}
