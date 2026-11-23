using System;
using System.Collections.Generic;
using System.Linq;
using DeezNET.Data;
using Moq;
using NzbDrone.Core.Download.Clients.Deezer.Queue;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Plugin.Deezer.HealthChecks;
using Shouldly;

namespace Lidarr.Plugin.Deezer.Test
{
    public class QualityFallbackTests
    {
        private const int AlbumId = 7;

        private readonly QualityFallbackTracker _tracker = new();
        private readonly Mock<IMediaFileService> _mediaFiles = new();

        private static QualityFallback Fallback(int albumId = AlbumId, Bitrate requested = Bitrate.FLAC, DateTime? recordedAt = null) =>
            new(albumId, "Trivium", "In Waves", requested, 12, new Dictionary<Bitrate, int> { [Bitrate.MP3_320] = 3 })
            {
                RecordedAt = recordedAt ?? DateTime.UtcNow
            };

        private QualityFallbackCheck CreateCheck() => new(_tracker, _mediaFiles.Object, null);

        private void SetFiles(params Quality[] qualities) =>
            _mediaFiles.Setup(m => m.GetFilesByAlbum(AlbumId))
                .Returns(qualities.Select(q => new TrackFile { AlbumId = AlbumId, Quality = new QualityModel(q) }).ToList());

        [Fact]
        public void NoLowerTracksIsNotAFallback()
        {
            DownloadItem.BuildQualityFallback(AlbumId, "a", "b", Bitrate.FLAC, 12, new Dictionary<Bitrate, int>()).ShouldBeNull();
        }

        [Fact]
        public void UnknownAlbumIsNotRecorded()
        {
            // without a Lidarr album the check could never clear it
            DownloadItem.BuildQualityFallback(0, "a", "b", Bitrate.FLAC, 12, new Dictionary<Bitrate, int> { [Bitrate.MP3_320] = 1 }).ShouldBeNull();
        }

        [Fact]
        public void FallbackKeepsItsOwnCopyOfTheCounts()
        {
            var counts = new Dictionary<Bitrate, int> { [Bitrate.MP3_320] = 2 };
            var fallback = DownloadItem.BuildQualityFallback(AlbumId, "a", "b", Bitrate.FLAC, 12, counts);
            counts[Bitrate.MP3_128] = 5;

            fallback.LowerTrackCount.ShouldBe(2);
        }

        [Fact]
        public void MessageListsTheAlbum()
        {
            var fallback = Fallback() with
            {
                LowerTracks = new Dictionary<Bitrate, int> { [Bitrate.MP3_128] = 1, [Bitrate.MP3_320] = 3 }
            };

            QualityFallbackCheck.BuildMessage(new[] { fallback })
                .ShouldEndWith("Trivium - In Waves (3 as MP3 320, 1 as MP3 128 of 12 tracks, grabbed as FLAC)");
        }

        [Fact]
        public void MessageCapsTheList()
        {
            var fallbacks = Enumerable.Range(1, 7).Select(i => Fallback(i)).ToList();

            var message = QualityFallbackCheck.BuildMessage(fallbacks);

            message.ShouldStartWith("7 Deezer downloads");
            message.ShouldEndWith("; and 2 more");
        }

        [Fact]
        public void NotResolvedBeforeImport()
        {
            QualityFallbackCheck.IsResolved(Array.Empty<Quality>(), Bitrate.FLAC).ShouldBeFalse();
        }

        [Fact]
        public void NotResolvedWhileAnyFileIsLower()
        {
            QualityFallbackCheck.IsResolved(new[] { Quality.FLAC, Quality.MP3_320 }, Bitrate.FLAC).ShouldBeFalse();
        }

        [Fact]
        public void ResolvedOnceEveryFileMeetsTheGrab()
        {
            // any lossless format counts for a FLAC grab, and better than the grab counts too
            QualityFallbackCheck.IsResolved(new[] { Quality.FLAC, Quality.FLAC_24, Quality.ALAC }, Bitrate.FLAC).ShouldBeTrue();
            QualityFallbackCheck.IsResolved(new[] { Quality.MP3_320, Quality.FLAC }, Bitrate.MP3_320).ShouldBeTrue();
        }

        [Fact]
        public void CheckIsOkWithNothingRecorded()
        {
            CreateCheck().Check().Type.ShouldBe(HealthCheckResult.Ok);
        }

        [Fact]
        public void CheckShowsANoticeUntilUpgraded()
        {
            _tracker.Record(Fallback());
            SetFiles(Quality.FLAC, Quality.MP3_320);

            var result = CreateCheck().Check();

            result.Type.ShouldBe(HealthCheckResult.Notice);
            result.Message.ShouldContain("Trivium - In Waves");
            _tracker.All().ShouldHaveSingleItem();
        }

        [Fact]
        public void CheckClearsAnUpgradedAlbum()
        {
            _tracker.Record(Fallback());
            SetFiles(Quality.FLAC, Quality.FLAC);

            CreateCheck().Check().Type.ShouldBe(HealthCheckResult.Ok);
            _tracker.All().ShouldBeEmpty();
        }

        [Fact]
        public void CheckClearsOldEntries()
        {
            _tracker.Record(Fallback(recordedAt: DateTime.UtcNow.AddDays(-15)));
            SetFiles(Quality.MP3_320);

            CreateCheck().Check().Type.ShouldBe(HealthCheckResult.Ok);
            _tracker.All().ShouldBeEmpty();
        }
    }
}
