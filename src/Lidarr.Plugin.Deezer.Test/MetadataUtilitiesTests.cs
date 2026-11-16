using Newtonsoft.Json.Linq;
using NzbDrone.Core.Download.Clients.Deezer;
using Shouldly;

namespace Lidarr.Plugin.Deezer.Test
{
    public class MetadataUtilitiesTests
    {
        // shaped like Deezer's deezer.pageTrack and deezer.pageAlbum responses, which send numbers as strings
        private static JObject TrackPage(string title = "Song", string version = "", string releaseDate = "2024-12-06", string trackNumber = "3") => new()
        {
            ["DATA"] = new JObject
            {
                ["SNG_TITLE"] = title,
                ["VERSION"] = version,
                ["ALB_TITLE"] = "Album",
                ["PHYSICAL_RELEASE_DATE"] = releaseDate,
                ["ART_NAME"] = "Track Artist",
                ["ARTISTS"] = new JArray(new JObject { ["ART_NAME"] = "Track Artist" }, new JObject { ["ART_NAME"] = "Guest" }),
                ["TRACK_NUMBER"] = trackNumber,
            },
        };

        private static JObject AlbumPage(string version = "", string releaseDate = "2024-12-06") => new()
        {
            ["DATA"] = new JObject
            {
                ["VERSION"] = version,
                ["PHYSICAL_RELEASE_DATE"] = releaseDate,
                ["ART_NAME"] = "Album Artist",
                ["ARTISTS"] = new JArray(new JObject { ["ART_NAME"] = "Album Artist" }),
            },
            ["SONGS"] = new JObject { ["total"] = 9 },
        };

        [Fact]
        public void FillsTheDefaultFileTemplate()
        {
            MetadataUtilities.GetFilledTemplate("%track% - %title%.%ext%", "flac", TrackPage(), AlbumPage())
                .ShouldBe("03 - Song.flac");
        }

        [Fact]
        public void KeepsTheTemplatesOwnFolderSeparators()
        {
            MetadataUtilities.GetFilledTemplate("%albumartist%/%album%/", "flac", TrackPage(), AlbumPage())
                .ShouldBe("Album Artist/Album/");
        }

        [Fact]
        public void AppendsTrackAndAlbumVersions()
        {
            MetadataUtilities.GetFilledTemplate("%album% - %title%", "flac", TrackPage(version: "(Live)"), AlbumPage(version: "(Deluxe)"))
                .ShouldBe("Album (Deluxe) - Song (Live)");
        }

        [Fact]
        public void FillsArtistListsAndCounts()
        {
            MetadataUtilities.GetFilledTemplate("%artists% | %albumartists% | %artist% | %trackcount% | %year%", "mp3", TrackPage(), AlbumPage())
                .ShouldBe("Track Artist; Guest | Album Artist | Track Artist | 9 | 2024");
        }

        [Fact]
        public void ReplacesPathSeparatorsInValues()
        {
            // '/' is invalid in a file name on every platform; the folder template's own separators are kept
            MetadataUtilities.GetFilledTemplate("%title%", "flac", TrackPage(title: "Either/Or"), AlbumPage())
                .ShouldBe("Either_Or");
        }

        [Fact]
        public void ZeroReleaseDateDoesNotFailTheTrack()
        {
            // Deezer sends 0000-00-00 for some releases; this used to throw before the track downloaded
            MetadataUtilities.GetFilledTemplate("%track% - %title%.%ext%", "flac", TrackPage(releaseDate: "0000-00-00"), AlbumPage(releaseDate: "0000-00-00"))
                .ShouldBe("03 - Song.flac");
        }

        [Fact]
        public void YearFallsBackToTheAlbumDate()
        {
            MetadataUtilities.GetFilledTemplate("%year%", "flac", TrackPage(releaseDate: "0000-00-00"), AlbumPage(releaseDate: "2019-05-01"))
                .ShouldBe("2019");
        }

        [Theory]
        [InlineData("2024-12-06", "2024")]
        [InlineData("1999", "1999")]
        [InlineData("0000-00-00", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        [InlineData("n/a", null)]
        [InlineData("-123-01-01", null)]
        public void ParseYear(string date, string expected)
        {
            MetadataUtilities.ParseYear(date).ShouldBe(expected);
        }
    }
}
