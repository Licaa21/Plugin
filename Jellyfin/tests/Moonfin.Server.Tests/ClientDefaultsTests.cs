using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The Moonbase page draws the TV app settings into its categories from a list of keys inside configPage.html and
/// writes the values into DefaultUserSettings under those names. A key the profile does not have, or has
/// with another type, would make the whole configuration fail to save, so the list is checked against
/// the profile here. The profile's own merge, resolve and apply code loops over its properties, so the
/// new fields use the same paths as the old ones.
/// </summary>
public sealed class ClientDefaultsTests : IDisposable
{
    // Account state of the TV app, not preferences. The user decided the first eleven never go through the
    // plugin; the last three are device security switches (a default could turn certificate checking off or
    // require a PIN nobody set) and were kept out the same way.
    private static readonly string[] AccountState =
    {
        "pinCode", "pinCodeHash", "pinFailedAttempts", "pinLockedUntil", "kidsModeEnabled", "kidsPinHash",
        "kidsPinFailedAttempts", "kidsPinLockedUntil", "autoLoginServerId", "autoLoginUserId", "syncProfile",
        "allowInsecureCerts", "useMoonfinPlugin", "pinCodeProtection",
    };

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-client-defaults-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private sealed record SchemaEntry(string K, string T, string P, string C, string L, double? Min, double? Max, double? Step, List<List<string>>? O);

    private static string Page()
    {
        using var stream = typeof(MoonfinSettingsProfile).Assembly.GetManifestResourceStream("Moonfin.Server.Pages.configPage.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static List<SchemaEntry> Schema()
    {
        var html = Page();

        const string start = "var CLIENT_DEFAULTS_SCHEMA = [";
        var from = html.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "the schema is missing from configPage.html");
        from += start.Length - 1;
        var to = html.IndexOf("];", from, StringComparison.Ordinal) + 1;
        Assert.True(to > from, "the end of the schema in configPage.html was not found");

        return JsonSerializer.Deserialize<List<SchemaEntry>>(
            html[from..to],
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static Dictionary<string, PropertyInfo> ProfileByJsonName() =>
        typeof(MoonfinSettingsProfile).GetProperties()
            .ToDictionary(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name, p => p, StringComparer.Ordinal);

    [Fact]
    public void EveryKeyInTheTabIsAProfileSettingOfTheRightType()
    {
        var profile = ProfileByJsonName();
        var schema = Schema();
        Assert.True(schema.Count > 100);

        foreach (var entry in schema)
        {
            Assert.True(profile.TryGetValue(entry.K, out var property), $"{entry.K} is in the tab but not in the profile");
            var type = property!.PropertyType;
            var ok = entry.T switch
            {
                "bool" => type == typeof(bool?),
                "int" => type == typeof(int?) || type == typeof(long?) || type == typeof(double?),
                "num" => type == typeof(double?) || type == typeof(float?) || type == typeof(decimal?),
                "text" => type == typeof(string),
                "list" => type == typeof(List<string>),
                "order" => type == typeof(List<string>),
                _ => false,
            };
            Assert.True(ok, $"{entry.K}: the tab treats it as {entry.T}, the profile has {type.Name}");

            // A whole-number setting cannot be driven in halves or tenths.
            foreach (var number in new[] { entry.Min, entry.Max, entry.Step })
            {
                Assert.True(entry.T is not "int" || number is null || number == Math.Floor(number.Value), $"{entry.K}: whole number setting with a fractional range");
            }
        }
    }

    [Fact]
    public void EveryChoiceOfANumberSettingIsANumberInsideItsRange()
    {
        // A choice that does not parse would be saved as "not set" without a word.
        foreach (var entry in Schema().Where(e => e.O != null && (e.T is "int" or "num")))
        {
            foreach (var choice in entry.O!)
            {
                Assert.True(
                    double.TryParse(choice[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number),
                    $"{entry.K}: the choice '{choice[0]}' is not a number");
                Assert.True(entry.T != "int" || number == Math.Floor(number), $"{entry.K}: the choice '{choice[0]}' is not a whole number");
                Assert.True(
                    (entry.Min is null || number >= entry.Min) && (entry.Max is null || number <= entry.Max),
                    $"{entry.K}: the choice '{choice[0]}' is outside {entry.Min} to {entry.Max}, the editor would move it on save");
            }
        }
    }

    [Fact]
    public void TheTwoSettingsStoredAsTextOfferTheFormsTheOtherMoonfinAppWrites()
    {
        // The TV app writes the bitrate as Mbps text with "0" for Auto and the timeout as m<minutes> or s<seconds>;
        // Moonfin-Core writes "auto" for Auto and only m<minutes>. Each client reads only its own forms, so the
        // editor offers the forms of both, and nothing a client could not read.
        var schema = Schema();

        Assert.Equal(
            new[] { "0", "auto", "200", "120", "80", "60", "40", "20", "10", "5", "2", "1" },
            schema.Single(e => e.K == "maxBitrate").O!.Select(o => o[0]));
        Assert.Equal(
            new[] { "s30", "m1", "s90", "m2", "m3", "m5", "m10", "m15", "m30" },
            schema.Single(e => e.K == "screensaverTimeout").O!.Select(o => o[0]));

        // The shape, not only the list: a bitrate is a short count of Mbps (never bits per second, which an
        // earlier version offered) and a timeout is a unit letter and a count.
        Assert.All(
            schema.Single(e => e.K == "maxBitrate").O!,
            o => Assert.Matches("^(auto|[0-9]{1,3})$", o[0]));
        Assert.All(
            schema.Single(e => e.K == "screensaverTimeout").O!,
            o => Assert.Matches("^[smh][0-9]{1,4}$", o[0]));
    }

    [Fact]
    public void TheHomeRowImageTypeControlsShowInBothRowStylesAndOfferBackdropAndLogo()
    {
        // The TV app uses these for Modern cards as well, so no row style hides them, and it offers five artwork types.
        var html = Page();

        var rule = html.Split('\n').Single(l => l.Contains("parent: 'DefaultHomeRowsStyle', off: ['v2']"));
        Assert.DoesNotContain("DefaultHomeImageTypeContinueWatching", rule);
        Assert.DoesNotContain("DefaultHomeRowsImageTypeOverride", rule);

        var from = html.IndexOf("id=\"DefaultHomeRowsImageType\"", StringComparison.Ordinal);
        var rowType = html[from..html.IndexOf("</select>", from, StringComparison.Ordinal)];
        foreach (var value in new[] { "poster", "backdrop", "logo", "thumb", "banner" })
        {
            Assert.Contains($"value=\"{value}\"", rowType);
            Assert.Contains($"<option value=\"{value}\">", html[html.IndexOf("var HOME_ROW_IMAGE_TYPE_OPTIONS", StringComparison.Ordinal)..][..600]);
        }
    }

    // Categories the page does not have, so the first setting that goes there creates them (the app's own screen names).
    private static readonly HashSet<string> CategoriesTheTabCreates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Quality & Decoding", "Subtitle Customization", "Accent Colors", "Sign-in", "Diagnostics",
    };

    private static string PanelOf(string html, string subtab)
    {
        var match = System.Text.RegularExpressions.Regex.Match(html, $"class=\"defaultsSubtabPanel[^\"]*\" data-subtab=\"{subtab}\"");
        Assert.True(match.Success, $"the page has no '{subtab}' subtab");
        var end = html.IndexOf("class=\"defaultsSubtabPanel", match.Index + match.Length, StringComparison.Ordinal);
        var tab = html.IndexOf("<section class=\"moonfinTabPanel", match.Index + match.Length, StringComparison.Ordinal);
        end = end < 0 ? tab : tab < 0 ? end : Math.Min(end, tab);
        return html[match.Index..end];
    }

    [Fact]
    public void EverySettingSitsInASubtabOfThePageAndInACategoryItHasOrCreates()
    {
        // Each setting is drawn into the category of that name in its subtab; a misspelt name would quietly make a second
        // category next to the page's own, and a missing subtab would leave the setting out.
        var html = Page();

        foreach (var group in Schema().GroupBy(e => e.P))
        {
            var panel = PanelOf(html, group.Key);
            var headings = System.Text.RegularExpressions.Regex.Matches(panel, "<h4[^>]*>([^<]*)</h4>")
                .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in group)
            {
                Assert.True(
                    headings.Contains(entry.C) || CategoriesTheTabCreates.Contains(entry.C),
                    $"{entry.K}: '{entry.C}' is neither a category of the '{entry.P}' subtab nor one the tab creates");
            }
        }
    }

    [Fact]
    public void TheSeerrOnlySettingsFollowTheSeerrSwitch()
    {
        var html = Page();
        var rule = html.Split('\n').Single(l => l.Contains("{ parent: 'SeerrEnabled', fields: ["));

        foreach (var entry in Schema().Where(e => e.P == "integrations" && e.C == "Seerr"))
        {
            Assert.Contains($"'ClientDefault_{entry.K}'", rule);
        }
    }

    [Fact]
    public void ThereIsNoSeparateSubtabForTheTvAppSettingsAndTheAudioCodecOrderIsASortableList()
    {
        var html = Page();

        Assert.DoesNotContain("data-subtab=\"client-all\"", html);
        Assert.DoesNotContain("ClientDefaultsRoot", html);

        var order = Schema().Single(e => e.K == "audioCodecOrder");
        Assert.Equal("order", order.T);
        Assert.NotNull(order.O);
        Assert.Contains("eac3", order.O!.Select(o => o[0]));
    }

    [Theory]
    [InlineData("DETAIL_BUTTONS", "collection")]
    [InlineData("DETAIL_BUTTONS", "playbackInfo")]
    [InlineData("DETAIL_BUTTONS", "artwork")]
    [InlineData("OSD_BUTTONS", "episodes")]
    [InlineData("OSD_BUTTONS", "sleep")]
    public void TheButtonListsHaveTheButtonsOnlyTheTvAppHas(string list, string id)
    {
        // A button the list does not know is dropped from the stored order and hidden list when the page is saved.
        var html = Page();
        var from = html.IndexOf($"var {list} = [", StringComparison.Ordinal);
        Assert.True(from >= 0, $"{list} is missing");
        var block = html[from..html.IndexOf("];", from, StringComparison.Ordinal)];

        Assert.Contains($"id: '{id}'", block);
    }

    [Fact]
    public void TheTabListsEachKeyOnceAndEveryEntryHasALabelAndAGroup()
    {
        var schema = Schema();

        Assert.Equal(schema.Count, schema.Select(e => e.K).Distinct(StringComparer.Ordinal).Count());
        Assert.All(schema, e => Assert.False(string.IsNullOrWhiteSpace(e.L) || string.IsNullOrWhiteSpace(e.P) || string.IsNullOrWhiteSpace(e.C), e.K));
        Assert.Equal(schema.Count, schema.Select(e => e.P + "|" + e.C + "|" + e.L).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NothingTheTabCoversHasAControlOnTheOldTabsToo()
    {
        // Two controls for one setting would overwrite each other on save. The page reads a control
        // through "defaults.<key>" and writes it through "DefaultUserSettings.<key>"; those are the two
        // patterns this looks for, plus bracket access, so a control written some other way is not caught here.
        var html = Page();
        var from = html.IndexOf("var CLIENT_DEFAULTS_SCHEMA = [", StringComparison.Ordinal);
        var rest = html[..from] + html[html.IndexOf("];", from, StringComparison.Ordinal)..];

        foreach (var entry in Schema())
        {
            Assert.DoesNotMatch($@"\b(defaults|DefaultUserSettings)(\.{entry.K}\b|\[['""]{entry.K}['""]\])", rest);
        }
    }

    [Fact]
    public void AccountStateIsNeitherAProfileSettingNorInTheTab()
    {
        var profile = ProfileByJsonName();
        var tab = Schema().Select(e => e.K).ToHashSet(StringComparer.Ordinal);

        foreach (var key in AccountState)
        {
            Assert.DoesNotContain(key, profile.Keys);
            Assert.DoesNotContain(key, tab);
        }
    }

    [Fact]
    public void ASettingTheAppDrivesInHalvesIsNotAWholeNumber()
    {
        var profile = JsonSerializer.Deserialize<MoonfinSettingsProfile>("""{"videoStartDelay":0.5}""")!;

        Assert.Equal(0.5, profile.VideoStartDelay);
    }

    [Fact]
    public void TheAppsNamesDeserializeIntoTheProfile()
    {
        var json = """
            {"preferTranscode":true,"seekStep":15,"subtitleShadowBlur":0.3,"audioCodecOrder":["eac3","ac3"],
             "subtitleSize":"large","playlistsGroupByType":true,"detailButtonsMaxVisible":6,"uiScale":0.9,"settingsScale":0.8}
            """;

        var profile = JsonSerializer.Deserialize<MoonfinSettingsProfile>(json)!;

        Assert.True(profile.PreferTranscode);
        Assert.Equal(15, profile.SeekStep);
        Assert.Equal(0.3, profile.SubtitleShadowBlur);
        Assert.Equal(new[] { "eac3", "ac3" }, profile.AudioCodecOrder);
        Assert.Equal("large", profile.SubtitleSize);
        Assert.True(profile.PlaylistsGroupByType);
        Assert.Equal(6, profile.DetailButtonsMaxVisible);
        Assert.Equal(0.9, profile.UiScale);
        Assert.Equal(0.8, profile.SettingsScale);
    }

    [Fact]
    public async Task TheNewDefaultsGoToOneUserAndOnlyWhereTheDefaultsHaveAValue()
    {
        var service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _dataPath);
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        await service.SaveProfileAsync(
            user, "global",
            new MoonfinSettingsProfile { PreferTranscode = false, SubtitleSize = "small", Use24HourClock = true },
            "test-seed", notifySettingsChanged: false);
        await service.SaveProfileAsync(
            other, "global", new MoonfinSettingsProfile { SubtitleSize = "small" }, "test-seed", notifySettingsChanged: false);

        var defaults = new MoonfinSettingsProfile
        {
            PreferTranscode = true,
            SeekStep = 15,
            AudioCodecOrder = new List<string> { "eac3", "ac3" },
        };
        await service.MergeDefaultsToUserAsync(user, defaults);

        var changed = (await service.GetResolvedProfileAsync(user, "global"))!;
        Assert.True(changed.PreferTranscode);
        Assert.Equal(15, changed.SeekStep);
        Assert.Equal(new[] { "eac3", "ac3" }, changed.AudioCodecOrder);
        Assert.Equal("small", changed.SubtitleSize);
        Assert.True(changed.Use24HourClock);

        var untouched = (await service.GetResolvedProfileAsync(other, "global"))!;
        Assert.Null(untouched.PreferTranscode);
        Assert.Null(untouched.SeekStep);
        Assert.Null(untouched.AudioCodecOrder);
    }

    [Fact]
    public async Task ADeviceProfileStillBeatsTheGlobalOneForTheNewSettings()
    {
        var service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _dataPath);
        var user = Guid.NewGuid();
        await service.SaveProfileAsync(user, "global", new MoonfinSettingsProfile { SeekStep = 10, PreferTranscode = true }, "test-seed", notifySettingsChanged: false);
        await service.SaveProfileAsync(user, "tv", new MoonfinSettingsProfile { SeekStep = 30 }, "test-seed", notifySettingsChanged: false);

        var tv = (await service.GetResolvedProfileAsync(user, "tv"))!;

        Assert.Equal(30, tv.SeekStep);
        Assert.True(tv.PreferTranscode);
    }
}
