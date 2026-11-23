using System.Collections.Generic;
using DeezNET.Data;
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
