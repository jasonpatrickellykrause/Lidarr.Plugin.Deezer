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
    }
}
