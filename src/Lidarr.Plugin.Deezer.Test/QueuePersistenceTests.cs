using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezNET;
using DeezNET.Data;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Deezer.Queue;
using Shouldly;

namespace Lidarr.Plugin.Deezer.Test
{
    public class QueuePersistenceTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "deezer-queue-test-" + Guid.NewGuid().ToString("N"));
        private readonly Logger _logger = LogManager.CreateNullLogger();

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private static SavedDownload Saved(DownloadItemStatus status, string id = "id-1", DateTime? changed = null, string folder = "/downloads/Artist/Album [FLAC]") => new()
        {
            Id = id,
            DeezerUrl = "https://www.deezer.com/album/12345",
            DeezerAlbumId = 12345,
            Bitrate = Bitrate.FLAC,
            Status = status,
            StatusChangedAt = changed ?? Now.AddHours(-1),
            Message = status == DownloadItemStatus.Failed ? "2 of 10 tracks failed to download." : null,
            Title = "Album",
            Artist = "Artist",
            Explicit = true,
            DownloadFolder = folder,
            TotalSize = 1000,
            AlbumId = 42,
            MusicBrainzIds = new MusicBrainzIds { ReleaseGroupId = "rg", ReleaseId = "rel" },
            RecordingIds = new[] { "rec-1", "rec-2" },
        };

        private DownloadQueueStore Store() => new(Path.Combine(_folder, "Deezer", "queue.json"), _logger);

        [Fact]
        public void Saved_items_round_trip_with_the_same_id()
        {
            var store = Store();
            var items = new[]
            {
                DownloadItem.FromSaved(Saved(DownloadItemStatus.Queued, "a")),
                DownloadItem.FromSaved(Saved(DownloadItemStatus.Failed, "b")),
            };

            store.Save(items);
            var loaded = store.Load(Now, _ => true);

            loaded.Select(i => i.ID).ShouldBe(new[] { "a", "b" });
            var failed = loaded[1];
            failed.Status.ShouldBe(DownloadItemStatus.Failed);
            failed.Message.ShouldBe("2 of 10 tracks failed to download.");
            failed.Title.ShouldBe("Album");
            failed.Artist.ShouldBe("Artist");
            failed.Explicit.ShouldBeTrue();
            failed.Bitrate.ShouldBe(Bitrate.FLAC);
            failed.AlbumId.ShouldBe(42);
            failed.TotalSize.ShouldBe(1000);
            failed.MusicBrainzIds.ReleaseId.ShouldBe("rel");

            var resaved = failed.ToSaved();
            resaved.DeezerUrl.ShouldBe("https://www.deezer.com/album/12345");
            resaved.DeezerAlbumId.ShouldBe(12345);
            resaved.RecordingIds.ShouldBe(new[] { "rec-1", "rec-2" });
        }

        [Fact]
        public void A_running_download_restarts_as_queued()
        {
            var item = DownloadItem.FromSaved(Saved(DownloadItemStatus.Downloading));

            item.Status.ShouldBe(DownloadItemStatus.Queued);
            item.DownloadedSize.ShouldBe(0);
        }

        [Fact]
        public void A_completed_download_reports_as_fully_downloaded()
        {
            var item = DownloadItem.FromSaved(Saved(DownloadItemStatus.Completed));

            item.Status.ShouldBe(DownloadItemStatus.Completed);
            item.Progress.ShouldBe(1);
        }

        [Fact]
        public void A_failed_item_keeps_the_time_it_failed()
        {
            var failedAt = Now.AddDays(-2);

            DownloadItem.FromSaved(Saved(DownloadItemStatus.Failed, changed: failedAt)).StatusChangedAt.ShouldBe(failedAt);
        }

        [Theory]
        [InlineData(DownloadItemStatus.Queued, true)]
        [InlineData(DownloadItemStatus.Downloading, true)]
        [InlineData(DownloadItemStatus.Paused, false)]
        public void Queued_and_running_downloads_are_restored(DownloadItemStatus status, bool expected)
        {
            DownloadQueueStore.ShouldRestore(Saved(status), Now, _ => false).ShouldBe(expected);
        }

        [Fact]
        public void A_completed_download_is_restored_only_while_its_folder_has_audio()
        {
            var saved = Saved(DownloadItemStatus.Completed);

            DownloadQueueStore.ShouldRestore(saved, Now, _ => true).ShouldBeTrue();
            DownloadQueueStore.ShouldRestore(saved, Now, _ => false).ShouldBeFalse();
            DownloadQueueStore.ShouldRestore(Saved(DownloadItemStatus.Completed, folder: null), Now, _ => true).ShouldBeFalse();
        }

        [Theory]
        [InlineData(DownloadItemStatus.Failed, 6, true)]
        [InlineData(DownloadItemStatus.Failed, 8, false)]
        [InlineData(DownloadItemStatus.Warning, 6, true)]
        [InlineData(DownloadItemStatus.Warning, 8, false)]
        public void Failed_and_warning_items_are_dropped_after_a_week(DownloadItemStatus status, int daysAgo, bool expected)
        {
            DownloadQueueStore.ShouldRestore(Saved(status, changed: Now.AddDays(-daysAgo)), Now, _ => true).ShouldBe(expected);
        }

        [Fact]
        public void An_entry_without_a_deezer_album_is_skipped()
        {
            var saved = Saved(DownloadItemStatus.Queued);
            saved.DeezerAlbumId = 0;

            DownloadQueueStore.ShouldRestore(saved, Now, _ => true).ShouldBeFalse();
        }

        [Fact]
        public void A_missing_file_loads_an_empty_queue()
        {
            Store().Load(Now, _ => true).ShouldBeEmpty();
        }

        [Fact]
        public void A_corrupt_file_loads_an_empty_queue()
        {
            var store = Store();
            Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
            File.WriteAllText(store.Path, "{ not json");

            store.Load(Now, _ => true).ShouldBeEmpty();
        }

        [Fact]
        public void Saving_replaces_the_file_and_leaves_no_temp_file()
        {
            var store = Store();
            store.Save(new[] { DownloadItem.FromSaved(Saved(DownloadItemStatus.Queued, "a")) });
            store.Save(new[] { DownloadItem.FromSaved(Saved(DownloadItemStatus.Queued, "b")) });

            store.Load(Now, _ => true).Single().ID.ShouldBe("b");
            File.Exists(store.Path + ".tmp").ShouldBeFalse();
        }

        [Fact]
        public void Restore_lists_items_and_skips_ids_already_queued()
        {
            var queue = new DownloadTaskQueue(10, null, _logger);
            var queued = DownloadItem.FromSaved(Saved(DownloadItemStatus.Queued, "a"));
            var completed = DownloadItem.FromSaved(Saved(DownloadItemStatus.Completed, "b"));

            queue.Restore(new[] { queued, completed });
            queue.Restore(new[] { DownloadItem.FromSaved(Saved(DownloadItemStatus.Queued, "a")) });

            queue.GetQueueListing().Select(i => i.ID).ShouldBe(new[] { "a", "b" });
            queue.GetTokenForItem(queued).CanBeCanceled.ShouldBeTrue();
        }

        [Fact]
        public void Removing_an_item_saves_and_cancels_it()
        {
            var saves = 0;
            var queue = new DownloadTaskQueue(10, null, _logger, onChanged: () => saves++);
            var item = DownloadItem.FromSaved(Saved(DownloadItemStatus.Queued));
            queue.Restore(new[] { item });
            var token = queue.GetTokenForItem(item);

            queue.RemoveItem(item);

            token.IsCancellationRequested.ShouldBeTrue();
            queue.GetQueueListing().ShouldBeEmpty();
            saves.ShouldBe(1);
        }

        [Fact]
        public async Task A_restored_download_waits_for_settings_instead_of_failing()
        {
            var queue = new DownloadTaskQueue(10, null, _logger);
            var item = DownloadItem.FromSaved(Saved(DownloadItemStatus.Downloading));
            queue.Restore(new[] { item });

            queue.StartQueueHandler();
            await Task.Delay(300);

            item.Status.ShouldBe(DownloadItemStatus.Queued);
            queue.Stop();
        }

        [Fact]
        public void Stop_cancels_downloads_and_stops_saving()
        {
            var saves = 0;
            var queue = new DownloadTaskQueue(10, null, _logger, onChanged: () => saves++);
            var item = DownloadItem.FromSaved(Saved(DownloadItemStatus.Queued));
            queue.Restore(new[] { item });
            var token = queue.GetTokenForItem(item);

            queue.Stop();
            queue.RemoveItem(item);

            queue.IsStopped.ShouldBeTrue();
            token.IsCancellationRequested.ShouldBeTrue();
            saves.ShouldBe(0);
        }
    }
}
