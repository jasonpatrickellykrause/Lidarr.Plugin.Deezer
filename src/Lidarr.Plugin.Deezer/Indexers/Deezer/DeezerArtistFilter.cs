using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Core.Download.Clients.Deezer;

namespace NzbDrone.Core.Indexers.Deezer
{
    // Deezer's gw search doesn't honour the artist:"..." filter, so a search for one album returns up to a page of
    // unrelated albums. Each result costs a paced album page lookup, so results whose credited artists don't include the
    // searched artist are dropped before those lookups run.
    public static class DeezerArtistFilter
    {
        private static readonly Regex ArtistFilterRegex = new("artist:\"(?<artist>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NonAlphanumericRegex = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

        // the artist named in a search query built by DeezerRequestGenerator, or null for the looser fallback query
        public static string GetSearchedArtist(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            var match = ArtistFilterRegex.Match(query);
            return match.Success ? match.Groups["artist"].Value : null;
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

            // whole-word containment either way, so "Becky Hill" matches "Becky Hill & MK" and "MK & Becky Hill"
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
