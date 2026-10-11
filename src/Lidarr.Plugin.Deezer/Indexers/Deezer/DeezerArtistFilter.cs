using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Core.Download.Clients.Deezer;

namespace NzbDrone.Core.Indexers.Deezer
{
    // Deezer's gw search usually finds nothing for the artist:"..." query, and the plain fallback query returns up to a
    // page of albums, many by other artists. Each result costs a paced album page lookup, so results whose credited
    // artists don't include the searched artist are dropped before those lookups run.
    public static class DeezerArtistFilter
    {
        private static readonly Regex NonAlphanumericRegex = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

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
