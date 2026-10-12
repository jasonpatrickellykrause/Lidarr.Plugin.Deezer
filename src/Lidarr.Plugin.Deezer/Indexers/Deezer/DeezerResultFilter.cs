using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.Clients.Deezer;

namespace NzbDrone.Core.Indexers.Deezer
{
    // Deezer's gw search usually finds nothing for the artist:"..." query, and the plain fallback query returns up to a
    // page of albums: the searched album, the artist's other releases, and albums by other artists. Each result costs a
    // paced album page lookup, and Lidarr rejects every result that isn't the searched album, so results by other
    // artists or with other titles are dropped before those lookups run.
    public static class DeezerResultFilter
    {
        private static readonly Regex NonAlphanumericRegex = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

        // the same threshold Lidarr uses for inexact album title matches (AlbumYearMatchingOptions.TitleFuzzThreshold)
        private const double TitleFuzzThreshold = 0.70;

        // edition and format notes Deezer adds to titles: "(Deluxe Edition)", "[Remastered]", " - EP", " - Single"
        private static readonly Regex TitleExtrasRegex = new(@"\([^)]*\)|\[[^\]]*\]|\s+-\s+(EP|Single)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsTitled(DeezerGwAlbum album, string title)
        {
            var searched = Normalize(StripTitleExtras(title));
            if (searched.Length == 0)
            {
                return true;
            }

            var candidate = Normalize(StripTitleExtras(album.AlbumTitle));

            // Lidarr falls back to a fuzzy title match when it maps a release to the searched album, so anything it
            // could accept that way is kept: small spelling differences, "vol" for "volume", a missing word
            return NamesMatch(candidate, searched) ||
                   (candidate.Length > 0 && candidate.FuzzyMatch(searched) >= TitleFuzzThreshold);
        }

        // Deezer often lists the title-track single under the album's name ("Exit Life", 1 track, beside "EXIT LIFE", 15
        // tracks). A result with fewer than half the tracks of the album's shortest MusicBrainz release can't be that
        // album; Lidarr would grab it and then reject the import with "Has missing tracks". Alternate editions with a few
        // tracks more or fewer still pass.
        public static bool HasEnoughTracks(DeezerGwAlbum album, int? minTrackCount)
        {
            if (minTrackCount is not > 0 ||
                !int.TryParse(album.TrackCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tracks) ||
                tracks <= 0)
            {
                return true;
            }

            return tracks * 2 >= minTrackCount.Value;
        }

        private static string StripTitleExtras(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var stripped = TitleExtrasRegex.Replace(title, " ");

            // a title that is nothing but a parenthetical keeps it
            return string.IsNullOrWhiteSpace(stripped) ? title : stripped;
        }

        public static bool IsCreditedTo(DeezerGwAlbum album, string artist)
        {
            var searched = Normalize(artist);
            if (searched.Length == 0)
            {
                return true;
            }

            var names = new List<string> { album.ArtistName };
            if (album.Artists != null)
            {
                names.AddRange(album.Artists.Select(a => a?.ArtistName));
            }

            return names.Any(name => NamesMatch(Normalize(name), searched));
        }

        private static bool NamesMatch(string candidate, string searched)
        {
            if (candidate.Length == 0)
            {
                return false;
            }

            // whole-word containment either way, so "Becky Hill" matches "Becky Hill & MK" and "MK & Becky Hill", and
            // "Rebecca" matches "Rebecca Remixed"
            return $" {candidate} ".Contains($" {searched} ", StringComparison.Ordinal) ||
                   $" {searched} ".Contains($" {candidate} ", StringComparison.Ordinal);
        }

        public static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var decomposed = name.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            var text = builder.ToString().ToLowerInvariant().Replace("&", " and ");
            text = NonAlphanumericRegex.Replace(text, " ").Trim();

            return text.StartsWith("the ", StringComparison.Ordinal) ? text[4..] : text;
        }
    }
}
