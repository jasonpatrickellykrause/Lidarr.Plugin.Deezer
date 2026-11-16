using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace NzbDrone.Core.Download.Clients.Deezer
{
    internal static class MetadataUtilities
    {
        public static string GetFilledTemplate(string template, string ext, JToken deezerPage, JToken deezerAlbumPage)
        {
            var songTitle = deezerPage["DATA"]!["SNG_TITLE"]!.ToString();
            var songVersion = deezerPage["DATA"]?["VERSION"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(songVersion))
                songTitle = $"{songTitle} {songVersion}";

            var albumTitle = deezerPage["DATA"]!["ALB_TITLE"]!.ToString();
            var albumVersion = deezerAlbumPage["DATA"]?["VERSION"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(albumVersion))
                albumTitle = $"{albumTitle} {albumVersion}";

            // DateTime.Parse threw on Deezer's 0000-00-00 dates, failing the track before it downloaded even though the
            // default templates don't use %year%
            var year = ParseYear(deezerPage["DATA"]?["PHYSICAL_RELEASE_DATE"]?.ToString())
                       ?? ParseYear(deezerAlbumPage["DATA"]?["PHYSICAL_RELEASE_DATE"]?.ToString())
                       ?? string.Empty;

            return GetFilledTemplate_Internal(template,
                songTitle,
                albumTitle,
                deezerAlbumPage["DATA"]!["ART_NAME"]!.ToString(),
                deezerPage["DATA"]!["ART_NAME"]!.ToString(),
                deezerAlbumPage["DATA"]!["ARTISTS"]!.Select(a => a["ART_NAME"]!.ToString()).ToArray(),
                deezerPage["DATA"]!["ARTISTS"]!.Select(a => a["ART_NAME"]!.ToString()).ToArray(),
                $"{(int)deezerPage["DATA"]!["TRACK_NUMBER"]!:00}",
                deezerAlbumPage["SONGS"]!["total"]!.ToString(),
                year,
                ext);
        }

        // the year from a yyyy-MM-dd date, or null for 0000 and anything unparseable
        internal static string ParseYear(string date)
        {
            if (date == null || date.Length < 4 ||
                !int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year <= 0)
            {
                return null;
            }

            return year.ToString(CultureInfo.InvariantCulture);
        }

        private static string GetFilledTemplate_Internal(string template, string title, string album, string albumArtist, string artist, string[] albumArtists, string[] artists, string track, string trackCount, string year, string ext)
        {
            StringBuilder t = new(template);
            ReplaceC("%title%", title);
            ReplaceC("%album%", album);
            ReplaceC("%albumartist%", albumArtist);
            ReplaceC("%artist%", artist);
            ReplaceC("%albumartists%", string.Join("; ", albumArtists));
            ReplaceC("%artists%", string.Join("; ", artists));
            ReplaceC("%track%", track);
            ReplaceC("%trackcount%", trackCount);
            ReplaceC("%ext%", ext);
            ReplaceC("%year%", year);

            return t.ToString();

            void ReplaceC(string o, string r)
            {
                t.Replace(o, CleanPath(r));
            }
        }

        public static string CleanPath(string str)
        {
            var invalid = Path.GetInvalidFileNameChars();
            for (var i = 0; i < invalid.Length; i++)
            {
                var c = invalid[i];
                str = str.Replace(c, '_');
            }
            return str;
        }
    }
}
