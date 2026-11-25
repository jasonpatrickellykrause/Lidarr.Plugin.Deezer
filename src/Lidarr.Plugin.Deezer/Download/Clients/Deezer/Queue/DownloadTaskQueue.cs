using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using DeezNET.Exceptions;
using NLog;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Common.Instrumentation.Extensions;

namespace NzbDrone.Core.Download.Clients.Deezer.Queue
{
    public class DownloadTaskQueue
    {
        private readonly Channel<DownloadItem> _queue;
        private readonly List<DownloadItem> _items;
        private readonly Dictionary<DownloadItem, CancellationTokenSource> _cancellationSources;

        private readonly List<Task> _runningTasks = new();
        private readonly object _lock = new();
        private readonly CancellationTokenSource _stopping = new();

        private DeezerSettings _settings;
        private readonly Logger _logger;
        private readonly Action _onArlRejected;
        private readonly Action<DownloadItem> _onCompleted;
        private readonly Action _onChanged;
        private readonly Action _beforeDownload;

        public DownloadTaskQueue(int capacity, DeezerSettings settings, Logger logger, Action onArlRejected = null, Action<DownloadItem> onCompleted = null, Action onChanged = null, Action beforeDownload = null)
        {
            _onArlRejected = onArlRejected;
            _onCompleted = onCompleted;
            _onChanged = onChanged;
            _beforeDownload = beforeDownload;
            BoundedChannelOptions options = new(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            };
            _queue = Channel.CreateBounded<DownloadItem>(options);
            _items = new();
            _cancellationSources = new();
            _settings = settings;
            _logger = logger;
        }

        public bool IsStopped => _stopping.IsCancellationRequested;

        public void SetSettings(DeezerSettings settings) => _settings = settings;

        public void StartQueueHandler()
        {
            Task.Run(() => BackgroundProcessing(_stopping.Token));
        }

        // Stops taking new items and cancels running downloads, which stay Downloading so a saved queue restarts them.
        // Lidarr restarts in the same process after a plugin install or update and builds a new queue; without this
        // the old one kept downloading alongside it.
        public void Stop()
        {
            List<CancellationTokenSource> sources;
            lock (_lock)
            {
                if (_stopping.IsCancellationRequested)
                    return;

                _stopping.Cancel();
                sources = _cancellationSources.Values.ToList();
            }

            foreach (var source in sources)
            {
                try
                {
                    source.Cancel();
                }
                catch (ObjectDisposedException) { }
            }
        }

        private async Task BackgroundProcessing(CancellationToken stoppingToken = default)
        {
            using SemaphoreSlim semaphore = new(1, 1);

            async Task HandleTask(DownloadItem item, Task task)
            {
                try
                {
                    await task;

                    if (item.ArlRejected)
                        _onArlRejected?.Invoke();

                    if (item.Status == DownloadItemStatus.Completed)
                        NotifyCompleted(item);
                }
                catch (TaskCanceledException) { }
                catch (OperationCanceledException) { }
                catch (InvalidARLException ex)
                {
                    // a bad ARL isn't the release's fault, so keep it out of Lidarr's failed handling and blocklist
                    item.Status = DownloadItemStatus.Warning;
                    item.Message = "Deezer rejected the ARL before the download started. Replace the ARL in the Deezer indexer, then remove this item and search again.";
                    _logger.Error("Deezer rejected the ARL for album " + item.Title);
                    _logger.Error(ex.ToString());
                    _onArlRejected?.Invoke();
                }
                catch (Exception ex)
                {
                    item.Status = DownloadItemStatus.Failed;
                    item.Message = "Download failed: " + ex.Message;
                    _logger.Error("Error while downloading Deezer album " + item.Title);
                    _logger.Error(ex.ToString());
                }
                finally
                {
                    semaphore.Release();
                    lock (_lock)
                        _runningTasks.Remove(task);

                    NotifyChanged();
                }
            }

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await semaphore.WaitAsync(stoppingToken);

                    var item = await DequeueAsync(stoppingToken);

                    // an item removed while it waited is still in the channel; skip it rather than download it unseen
                    CancellationToken token;
                    lock (_lock)
                    {
                        if (!_cancellationSources.TryGetValue(item, out var source))
                        {
                            semaphore.Release();
                            continue;
                        }

                        token = source.Token;
                    }

                    BeforeDownload();

                    item.Status = DownloadItemStatus.Downloading;
                    NotifyChanged();

                    var downloadTask = item.DoDownload(_settings, _logger, token);

                    lock (_lock)
                        _runningTasks.Add(HandleTask(item, downloadTask));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }

            List<Task> remainingTasks;
            lock (_lock)
                remainingTasks = _runningTasks.ToList();
            await Task.WhenAll(remainingTasks);
        }

        // reporting is best-effort; an exception here would otherwise reach HandleTask's catch and mark a good download Failed
        private void NotifyCompleted(DownloadItem item)
        {
            try
            {
                _onCompleted?.Invoke(item);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Couldn't report the completed Deezer download " + item.Title);
            }
        }

        // After Stop the state on disk is the one to restart from, so the cancelled downloads must not overwrite it.
        private void NotifyChanged()
        {
            if (_stopping.IsCancellationRequested)
                return;

            try
            {
                _onChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Couldn't save the Deezer download queue");
            }
        }

        private void BeforeDownload()
        {
            try
            {
                _beforeDownload?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Couldn't prepare the Deezer session before a download");
            }
        }

        public async ValueTask QueueBackgroundWorkItemAsync(DownloadItem workItem)
        {
            // registered before it reaches the channel, so the handler always finds its cancellation source
            lock (_lock)
            {
                _items.Add(workItem);
                _cancellationSources.Add(workItem, new CancellationTokenSource());
            }

            await _queue.Writer.WriteAsync(workItem);
            NotifyChanged();
        }

        // Adds items loaded from the saved queue. Queued items go back in line; the rest are only reported to Lidarr.
        public void Restore(IEnumerable<DownloadItem> items)
        {
            var toQueue = new List<DownloadItem>();
            lock (_lock)
            {
                foreach (var item in items)
                {
                    if (_items.Any(i => i.ID == item.ID))
                        continue;

                    _items.Add(item);
                    _cancellationSources.Add(item, new CancellationTokenSource());

                    if (item.Status == DownloadItemStatus.Queued)
                        toQueue.Add(item);
                }
            }

            foreach (var item in toQueue)
            {
                if (!_queue.Writer.TryWrite(item))
                    _logger.Warn("The Deezer download queue is full, so {0} - {1} wasn't restarted", item.Artist, item.Title);
            }
        }

        private async ValueTask<DownloadItem> DequeueAsync(CancellationToken cancellationToken)
        {
            var workItem = await _queue.Reader.ReadAsync(cancellationToken);
            return workItem;
        }

        public void RemoveItem(DownloadItem workItem)
        {
            if (workItem == null)
                return;

            lock (_lock)
            {
                if (_cancellationSources.Remove(workItem, out var source))
                    source.Cancel();

                _items.Remove(workItem);
            }

            NotifyChanged();
        }

        public DownloadItem[] GetQueueListing()
        {
            lock (_lock)
                return _items.ToArray();
        }

        public CancellationToken GetTokenForItem(DownloadItem item)
        {
            lock (_lock)
            {
                if (_cancellationSources.TryGetValue(item, out var src))
                    return src!.Token;
            }

            return default;
        }
    }
}
