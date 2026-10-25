using System.Text.Json;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The keyboard style travels in the synced profile, and each device type can pick its own.
/// </summary>
public sealed class KeyboardStyleProfileTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dataPath;
    private readonly MoonfinSettingsService _service;

    public KeyboardStyleProfileTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-keyboard-style-tests-" + Guid.NewGuid().ToString("N"));
        _service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private MoonfinUserSettings Read(Guid userId) =>
        JsonSerializer.Deserialize<MoonfinUserSettings>(
            File.ReadAllText(Path.Combine(_dataPath, userId + ".json")),
            CamelCase)!;

    [Fact]
    public void TheFieldUsesTheClientsWireName()
    {
        var json = JsonSerializer.Serialize(new MoonfinSettingsProfile { KeyboardStyle = "docked" });

        Assert.Contains("\"keyboardStyle\":\"docked\"", json);
        Assert.Equal("docked", JsonSerializer.Deserialize<MoonfinSettingsProfile>(json)!.KeyboardStyle);
    }

    [Fact]
    public async Task APushWithoutTheFieldKeepsWhatWasStored()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { KeyboardStyle = "native" },
            "new-client",
            notifySettingsChanged: false);

        // What an app from before keyboard styles sends.
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { DetailScreenStyle = "classic" },
            "old-client",
            notifySettingsChanged: false);

        Assert.Equal("native", Read(userId).GetProfile("tv")!.KeyboardStyle);
    }

    [Fact]
    public async Task ADeviceStyleWinsOverTheGlobalOne()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "global",
            new MoonfinSettingsProfile { KeyboardStyle = "native" },
            "client",
            notifySettingsChanged: false);
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { KeyboardStyle = "docked" },
            "client",
            notifySettingsChanged: false);

        Assert.Equal("docked", (await _service.GetResolvedProfileAsync(userId, "tv"))!.KeyboardStyle);
        Assert.Equal("native", (await _service.GetResolvedProfileAsync(userId, "mobile"))!.KeyboardStyle);
    }
}
