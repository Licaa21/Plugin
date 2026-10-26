using System;

namespace Moonfin.Tmdb;

/// <summary>
/// The TMDB image width a Thumb redirect points at. A client that sends no size, or one this
/// doesn't list, gets the wide w1280 it always got.
/// </summary>
public static class TmdbImageSize
{
    public const string Default = "w1280";

    public static string Choose(string? size)
    {
        foreach (var allowed in new[] { "w300", "w780", Default })
        {
            if (string.Equals(size, allowed, StringComparison.OrdinalIgnoreCase)) return allowed;
        }

        return Default;
    }
}
