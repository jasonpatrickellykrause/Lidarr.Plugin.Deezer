using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezNET.Data;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.Clients.Deezer.Queue;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Deezer;
using NzbDrone.Plugin.Deezer.HealthChecks;
using DeezerIndexerSettings = NzbDrone.Core.Indexers.Deezer.DeezerIndexerSettings;

namespace NzbDrone.Core.Download.Clients.Deezer
{
    public interface IDeezerProxy
    {
        List<DownloadClientItem> GetQueue(DeezerSettings settings);
        Task<string> Download(RemoteAlbum remoteAlbum, DeezerSettings settings);
        void RemoveFromQueue(string downloadId, DeezerSettings settings);
    }

    public class DeezerProxy : IDeezerProxy, IHandle<ApplicationStartedEvent>, IHandle<ApplicationShutdownRequested>, IDisposable
    {
        private readonly ICached<DateTime?> _startTimeCache;
        private readonly DownloadTaskQueue _taskQueue;
        private readonly DownloadQueueStore _store;
        private readonly IEventAggregator _eventAggregator;
        private readonly IMissingEditionTracker _missingEditionTracker;
        private readonly IQualityFallbackTracker _qualityFallbackTracker;
        private readonly Lazy<IIndexerFactory> _indexerFactory;
        private readonly Logger _logger;
        private readonly object _lifecycleLock = new();
        private volatile bool _restored;

        // The indexer factory is lazy because the Deezer indexer itself depends on this proxy.
        public DeezerProxy(ICacheManager cacheManager, IEventAggregator eventAggregator, IMissingEditionTracker missingEditionTracker, IQualityFallbackTracker qualityFallbackTracker, IAppFolderInfo appFolderInfo, Lazy<IIndexerFactory> indexerFactory, Logger logger)
        {
            _eventAggregator = eventAggregator;
            _missingEditionTracker = missingEditionTracker;
            _qualityFallbackTracker = qualityFallbackTracker;
            _indexerFactory = indexerFactory;
            _logger = logger;
            _startTimeCache = cacheManager.GetCache<DateTime?>(GetType(), "startTimes");
            _store = new DownloadQueueStore(Path.Combine(appFolderInfo.AppDataFolder, "Deezer", "queue.json"), logger);
            _taskQueue = new(500, null, logger, () => eventAggregator.PublishEvent(new DeezerArlRejectedEvent()), OnCompleted, SaveQueue, EnsureArl);

            _taskQueue.StartQueueHandler();
        }

        // Loaded once the container is fully built, because a restarted download needs the indexer's ARL.
        public void Handle(ApplicationStartedEvent message)
        {
            List<DownloadItem> items;
            lock (_lifecycleLock)
            {
                if (_restored || _taskQueue.IsStopped)
                    return;

                _restored = true;
                items = _store.Load(DateTime.UtcNow);
            }

            if (items.Count == 0)
                return;

            _taskQueue.Restore(items);
            SaveQueue();

            var restarted = items.Count(i => i.Status == DownloadItemStatus.Queued);
            _logger.Info("Restored {0} Deezer downloads from before the restart ({1} to download again, {2} waiting for Lidarr)", items.Count, restarted, items.Count - restarted);
        }

        public void Handle(ApplicationShutdownRequested message) => Stop();

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }

        // Saves first, then cancels, so the file keeps running downloads as Downloading and the next start resumes them.
        private void Stop()
        {
            lock (_lifecycleLock)
            {
                if (_taskQueue.IsStopped)
                    return;

                SaveQueue();
                _taskQueue.Stop();
            }
        }

        // Nothing is saved until the saved queue has been loaded, or a grab or shutdown before startup finished would
        // overwrite the file with a queue that doesn't have the restored items yet.
        private void SaveQueue()
        {
            if (_restored)
                _store.Save(_taskQueue.GetQueueListing());
        }

        // A restarted download can run before any search has given DeezerAPI the ARL, and an empty ARL fails the
        // validity check as if Deezer had rejected it.
        private void EnsureArl()
        {
            if (!string.IsNullOrEmpty(DeezerAPI.Instance.Client.ActiveARL))
                return;

            var arl = _indexerFactory.Value.All()
                .OrderByDescending(d => d.EnableRss || d.EnableAutomaticSearch || d.EnableInteractiveSearch)
                .Select(d => d.Settings)
                .OfType<DeezerIndexerSettings>()
                .Select(s => s.Arl)
                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));

            if (arl != null)
                DeezerAPI.Instance.CheckAndSetARL(arl);
        }

        public List<DownloadClientItem> GetQueue(DeezerSettings settings)
        {
            _taskQueue.SetSettings(settings);

            var listing = _taskQueue.GetQueueListing();
            var completed = listing.Where(x => x.Status == DownloadItemStatus.Completed);
            var queue = listing.Where(x => x.Status == DownloadItemStatus.Queued);
            var current = listing.Where(x => x.Status == DownloadItemStatus.Downloading);

            // failed items have to be reported, or Lidarr never blocklists the release, searches again, or removes the leftover files
            var failed = listing.Where(x => x.Status == DownloadItemStatus.Failed || x.Status == DownloadItemStatus.Warning);

            var result = completed.Concat(failed).Concat(current).Concat(queue).Where(x => x != null).Select(ToDownloadClientItem).ToList();

            return result;
        }

        public void RemoveFromQueue(string downloadId, DeezerSettings settings)
        {
            _taskQueue.SetSettings(settings);

            var item = _taskQueue.GetQueueListing().FirstOrDefault(a => a.ID == downloadId);
            if (item != null)
                _taskQueue.RemoveItem(item);
        }

        public async Task<string> Download(RemoteAlbum remoteAlbum, DeezerSettings settings)
        {
            _taskQueue.SetSettings(settings);

            var downloadItem = await DownloadItem.From(remoteAlbum);
            await _taskQueue.QueueBackgroundWorkItemAsync(downloadItem);

            if (downloadItem.MissingEdition != null)
            {
                _missingEditionTracker.Record(downloadItem.MissingEdition);
                _eventAggregator.PublishEvent(new DeezerEditionMissingEvent());
            }

            return downloadItem.ID;
        }

        private void OnCompleted(DownloadItem item)
        {
            if (item.QualityFallback == null)
                return;

            _qualityFallbackTracker.Record(item.QualityFallback);
            _eventAggregator.PublishEvent(new DeezerQualityFallbackEvent());
        }

        private DownloadClientItem ToDownloadClientItem(DownloadItem x)
        {
            var title = $"{x.Artist} - {x.Title} [WEB] {x.Bitrate}";
            if (x.Explicit)
            {
                title += " [Explicit]";
            }

            var item = new DownloadClientItem
            {
                DownloadId = x.ID,
                Title = title,
                TotalSize = x.TotalSize,
                RemainingSize = x.TotalSize - x.DownloadedSize,
                RemainingTime = GetRemainingTime(x),
                Status = x.Status,
                Message = x.Message,
                CanMoveFiles = true,
                CanBeRemoved = true,
            };

            if (x.DownloadFolder.IsNotNullOrWhiteSpace())
            {
                item.OutputPath = new OsPath(x.DownloadFolder);
            }

            return item;
        }

        private TimeSpan? GetRemainingTime(DownloadItem x)
        {
            if (x.Status is DownloadItemStatus.Completed or DownloadItemStatus.Failed or DownloadItemStatus.Warning)
            {
                _startTimeCache.Remove(x.ID);
                return null;
            }

            if (x.Progress == 0)
            {
                return null;
            }

            var started = _startTimeCache.Find(x.ID);
            if (started == null)
            {
                started = DateTime.UtcNow;
                _startTimeCache.Set(x.ID, started);
                return null;
            }

            var elapsed = DateTime.UtcNow - started;
            var progress = Math.Min(x.Progress, 1);

            return TimeSpan.FromTicks((long)(elapsed.Value.Ticks * (1 - progress) / progress));
        }
    }
}
