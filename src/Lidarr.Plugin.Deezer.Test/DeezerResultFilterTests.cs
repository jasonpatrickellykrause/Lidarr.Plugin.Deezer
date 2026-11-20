using System.Collections.Generic;
using NzbDrone.Core.Download.Clients.Deezer;
using NzbDrone.Core.Indexers.Deezer;
using Shouldly;

namespace Lidarr.Plugin.Deezer.Test
{
    public class DeezerResultFilterTests
    {
        private static DeezerGwAlbum Album(string title = "Album", string artist = "Artist", string trackCount = "10", params string[] credited)
        {
            var artists = new List<DeezerGwArtist>();
            foreach (var name in credited)
            {
                artists.Add(new DeezerGwArtist { ArtistName = name });
            }

            return new DeezerGwAlbum { AlbumTitle = title, ArtistName = artist, TrackCount = trackCount, Artists = artists };
        }

        [Theory]
        [InlineData("EXIT LIFE", "Exit Life")]
        [InlineData("Hybrid Theory (20th Anniversary Edition)", "Hybrid Theory")]
        [InlineData("Burn the Witch - Single", "Burn the Witch")]
        [InlineData("Selected Ambient Works [Remastered]", "Selected Ambient Works")]
        [InlineData("Un Verano Sín Ti", "Un verano sin ti")]
        [InlineData("The Black Parade", "Black Parade")]
        [InlineData("Rock & Roll", "Rock and Roll")]
        [InlineData("Greatest Hits Volum 1", "Greatest Hits Volume 1")]
        public void IsTitledKeepsTheSearchedAlbum(string deezerTitle, string searched)
        {
            DeezerResultFilter.IsTitled(Album(deezerTitle), searched).ShouldBeTrue();
        }

        [Theory]
        [InlineData("Meteora", "Hybrid Theory")]
        [InlineData("Minutes to Midnight", "Hybrid Theory")]
        public void IsTitledDropsOtherAlbums(string deezerTitle, string searched)
        {
            DeezerResultFilter.IsTitled(Album(deezerTitle), searched).ShouldBeFalse();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void IsTitledKeepsEverythingWhenNoTitleIsSearched(string searched)
        {
            DeezerResultFilter.IsTitled(Album("Anything"), searched).ShouldBeTrue();
        }

        [Theory]
        [InlineData(null, "1")]
        [InlineData(0, "1")]
        [InlineData(12, "not a number")]
        [InlineData(12, "0")]
        [InlineData(12, "6")]
        [InlineData(12, "15")]
        public void HasEnoughTracksKeepsPlausibleEditions(int? minTrackCount, string deezerTrackCount)
        {
            DeezerResultFilter.HasEnoughTracks(Album(trackCount: deezerTrackCount), minTrackCount).ShouldBeTrue();
        }

        [Theory]
        [InlineData(12, "1")]
        [InlineData(12, "5")]
        public void HasEnoughTracksDropsResultsWithUnderHalfTheTracks(int minTrackCount, string deezerTrackCount)
        {
            // the title-track single Deezer lists under the album's name
            DeezerResultFilter.HasEnoughTracks(Album(trackCount: deezerTrackCount), minTrackCount).ShouldBeFalse();
        }

        [Theory]
        [InlineData("Becky Hill", "Becky Hill")]
        [InlineData("Becky Hill & MK", "Becky Hill")]
        [InlineData("MK & Becky Hill", "Becky Hill")]
        [InlineData("Simon & Garfunkel", "Simon and Garfunkel")]
        [InlineData("Beyoncé", "Beyonce")]
        [InlineData("The Cure", "Cure")]
        public void IsCreditedToMatchesTheMainArtist(string deezerArtist, string searched)
        {
            DeezerResultFilter.IsCreditedTo(Album(artist: deezerArtist), searched).ShouldBeTrue();
        }

        [Fact]
        public void IsCreditedToMatchesAnyCreditedArtist()
        {
            DeezerResultFilter.IsCreditedTo(Album(artist: "DJ Snake", credited: new[] { "DJ Snake", "Lil Jon" }), "Lil Jon").ShouldBeTrue();
        }

        [Theory]
        [InlineData("MKTO", "MK")]
        [InlineData("Various Artists", "Becky Hill")]
        public void IsCreditedToDropsOtherArtists(string deezerArtist, string searched)
        {
            DeezerResultFilter.IsCreditedTo(Album(artist: deezerArtist), searched).ShouldBeFalse();
        }

        [Fact]
        public void IsCreditedToHandlesMissingCredits()
        {
            var album = new DeezerGwAlbum { ArtistName = "Artist", Artists = null };
            DeezerResultFilter.IsCreditedTo(album, "Artist").ShouldBeTrue();

            var withNullEntry = Album(artist: "Someone Else", credited: new string[] { null });
            DeezerResultFilter.IsCreditedTo(withNullEntry, "Artist").ShouldBeFalse();
        }

        [Theory]
        [InlineData("Crown In The Grave", "Crown in the Grave")]
        [InlineData("EXIT LIFE", "Exit Life")]
        [InlineData("Don’t Stop Me Now", "Don't Stop Me Now")]
        [InlineData("Un Verano Sín Ti", "Un verano sin ti")]
        public void AlignTitleUsesLidarrsSpellingWhenOnlyCaseOrPunctuationDiffers(string deezerTitle, string searched)
        {
            DeezerResultFilter.AlignTitle(deezerTitle, searched).ShouldBe(searched);
        }

        [Theory]
        [InlineData("Hybrid Theory (20th Anniversary Edition)", "Hybrid Theory")]
        [InlineData("Burn the Witch - Single", "Burn the Witch")]
        [InlineData("Meteora", "Hybrid Theory")]
        [InlineData("Crown in the Grave", "Crown in the Grave")]
        [InlineData("Crown In The Grave", null)]
        [InlineData("Crown In The Grave", "")]
        public void AlignTitleKeepsDeezersTitleOtherwise(string deezerTitle, string searched)
        {
            DeezerResultFilter.AlignTitle(deezerTitle, searched).ShouldBe(deezerTitle);
        }

        [Theory]
        [InlineData("The Beatles", "beatles")]
        [InlineData("AC/DC", "ac dc")]
        [InlineData("Sigur Rós", "sigur ros")]
        [InlineData("  Earth, Wind & Fire  ", "earth wind and fire")]
        [InlineData(null, "")]
        public void NormalizeFoldsCaseAccentsAndPunctuation(string name, string expected)
        {
            DeezerResultFilter.Normalize(name).ShouldBe(expected);
        }
    }
}
