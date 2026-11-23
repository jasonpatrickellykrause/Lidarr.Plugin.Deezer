using System;
using System.Collections.Generic;
using DeezNET.Data;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Download.Clients.Deezer.Queue;
using Shouldly;

namespace Lidarr.Plugin.Deezer.Test
{
    public class DownloadItemTests
    {
        [Fact]
        public void NoLyricsHaveNoSyncedLines()
        {
            DownloadItem.HasSyncedLines(null).ShouldBeFalse();
            DownloadItem.HasSyncedLines(new List<SyncLyrics>()).ShouldBeFalse();
        }

        [Fact]
        public void BlankLinesAreNotSyncedLines()
        {
            // Deezer includes entries without text for instrumental breaks; writing only these produced an empty .lrc
            var lyrics = new List<SyncLyrics>
            {
                new() { LrcTimestamp = "[00:10.00]", Line = "" },
                new() { LrcTimestamp = "", Line = "No timestamp" },
                new() { LrcTimestamp = null, Line = null },
            };

            DownloadItem.HasSyncedLines(lyrics).ShouldBeFalse();
        }

        [Fact]
        public void OneTimedLineIsEnough()
        {
            var lyrics = new List<SyncLyrics>
            {
                new() { LrcTimestamp = "[00:10.00]", Line = "" },
                new() { LrcTimestamp = "[00:19.33]", Line = "She is standing in the dark" },
            };

            DownloadItem.HasSyncedLines(lyrics).ShouldBeTrue();
        }

        [Fact]
        public void FirstTrackKeepsItsName()
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            DownloadItem.ClaimFileName(claimed, "01 - Intro", 1).ShouldBe("01 - Intro");
            DownloadItem.ClaimFileName(claimed, "02 - Outro", 1).ShouldBe("02 - Outro");
        }

        [Fact]
        public void SameNameOnAnotherDiscGetsTheDiscNumber()
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DownloadItem.ClaimFileName(claimed, "01 - Intro", 1);

            DownloadItem.ClaimFileName(claimed, "01 - Intro", 2).ShouldBe("2-01 - Intro");
        }

        [Fact]
        public void NamesDifferingOnlyInCaseCollide()
        {
            // one file on Windows and macOS
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DownloadItem.ClaimFileName(claimed, "01 - Intro", 1);

            DownloadItem.ClaimFileName(claimed, "01 - INTRO", 2).ShouldBe("2-01 - INTRO");
        }

        [Fact]
        public void StillCollidingNamesAreNumbered()
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "01 - Intro", "2-01 - Intro" };

            DownloadItem.ClaimFileName(claimed, "01 - Intro", 2).ShouldBe("2-01 - Intro (2)");
            DownloadItem.ClaimFileName(claimed, "01 - Intro", 2).ShouldBe("2-01 - Intro (3)");
        }

        [Theory]
        [InlineData("2", 2)]
        [InlineData("0", 1)]
        [InlineData("", 1)]
        [InlineData(null, 1)]
        public void DiscNumberDefaultsToOne(string value, int expected)
        {
            DownloadItem.ParseDisc(value == null ? null : JToken.FromObject(value)).ShouldBe(expected);
        }

        [Fact]
        public void AllFilesPresentPasses()
        {
            var files = new[] { "/a/01.flac", "/a/02.flac" };

            DownloadItem.GetMissingFilesMessage(files, 2, _ => true).ShouldBeNull();
        }

        [Fact]
        public void MissingFileFails()
        {
            var files = new[] { "/a/01.flac", "/a/02.flac" };

            DownloadItem.GetMissingFilesMessage(files, 2, f => f != "/a/02.flac")
                .ShouldBe("All 2 tracks downloaded, but the download folder has 1 of the 2 track files, so Lidarr would import an incomplete album.");
        }

        [Fact]
        public void SamePathTwiceCountsOnce()
        {
            // two tracks written to one path is the overwrite this check exists for
            var files = new[] { "/a/01 - Intro.flac", "/a/01 - intro.flac" };

            DownloadItem.GetMissingFilesMessage(files, 2, _ => true).ShouldNotBeNull();
        }

        [Fact]
        public void FailureMessageWithoutVerificationFailures()
        {
            DownloadItem.GetFailureMessage(2, 0, 10)
                .ShouldBe("2 of 10 tracks failed to download. The Lidarr log has the error for each track.");
        }

        [Fact]
        public void FailureMessageNamesVerificationFailures()
        {
            var message = DownloadItem.GetFailureMessage(3, 1, 10);

            message.ShouldStartWith("3 of 10 tracks failed to download. 1 downloaded but failed the audio check");
            message.ShouldContain("30-second preview");
        }
    }
}
