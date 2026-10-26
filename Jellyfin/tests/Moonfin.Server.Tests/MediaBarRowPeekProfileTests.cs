using System.Text.Json;
using Moonfin.Server.Models;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The row peek toggle travels in the synced profile under the clients' key.
/// </summary>
public sealed class MediaBarRowPeekProfileTests
{
    [Fact]
    public void TheFieldUsesTheClientsWireName()
    {
        var json = JsonSerializer.Serialize(new MoonfinSettingsProfile { MediaBarRowPeek = true });

        Assert.Contains("\"mediaBarRowPeek\":true", json);
        Assert.True(JsonSerializer.Deserialize<MoonfinSettingsProfile>(json)!.MediaBarRowPeek);
    }
}
