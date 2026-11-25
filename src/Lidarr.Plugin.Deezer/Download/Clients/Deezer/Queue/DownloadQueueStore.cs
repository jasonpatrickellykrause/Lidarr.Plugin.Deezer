using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeezNET;
using DeezNET.Data;
using Newtonsoft.Json;
using NLog;

namespace NzbDrone.Core.Download.Clients.Deezer.Queue
{
    // what's kept of a DownloadItem across a restart
    internal class SavedDownload
    {
        public string Id { get; set; }
        public string DeezerUrl { get; set; }
        public long DeezerAlbumId { get; set; }
        public Bitrate Bitrate { get; set; }
        public DownloadItemStatus Status { get; set; }
        public DateTime StatusChangedAt { get; set; }
        public string Message { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public bool Explicit { get; set; }
        public string DownloadFolder { get; set; }
        public long TotalSize { get; set; }
        public int AlbumId { get; set; }
        public MusicBrainzIds MusicBrainzIds { get; set; }
        public string[] RecordingIds { get; set; }
    }

    // The queue used to live only in memory, so every restart, including the one after installing or updating any
    // plugin, emptied Activity. Lidarr's history still said "grabbed", so those albums never arrived and were never
    // searched again. The queue is now saved to a JSON file on each change and loaded at startup.
    internal class DownloadQueueStore
    {
        // a failed item is normally removed by Lidarr once it has blocklisted the release; this drops any it didn't
        internal static readonly TimeSpan FailedRetention = TimeSpan.FromDays(7);

        private static readonly string[] AudioExtensions = { ".flac", ".mp3" };

        private readonly string _path;
        private readonly Logger _logger;
        private readonly object _fileLock = new();

        public DownloadQueueStore(string path, Logger logger)
        {
            _path = path;
            _logger = logger;
        }

        public string Path => _path;

        public void Save(IEnumerable<DownloadItem> items)
        {
            var saved = items.Where(i => i?.ID != null).Select(i => i.ToSaved()).ToList();

            lock (_fileLock)
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

                    // write the whole file, then swap it in, so a crash mid-write can't leave a truncated queue
                    var temp = _path + ".tmp";
                    File.WriteAllText(temp, JsonConvert.SerializeObject(saved, Formatting.Indented));
                    File.Move(temp, _path, true);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Couldn't save the Deezer download queue to {0}. Downloads still run, but a restart will drop them from Activity.", _path);
                }
            }
        }

        public List<DownloadItem> Load(DateTime utcNow) => Load(utcNow, FolderHasAudio);

        internal List<DownloadItem> Load(DateTime utcNow, Func<string, bool> folderHasAudio)
        {
            List<SavedDownload> saved;

            lock (_fileLock)
            {
                if (!File.Exists(_path))
                    return new List<DownloadItem>();

                try
                {
                    saved = JsonConvert.DeserializeObject<List<SavedDownload>>(File.ReadAllText(_path)) ?? new List<SavedDownload>();
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Couldn't read the saved Deezer download queue in {0}, starting with an empty queue", _path);
                    return new List<DownloadItem>();
                }
            }

            var restored = new List<DownloadItem>();
            foreach (var item in saved.Where(s => ShouldRestore(s, utcNow, folderHasAudio)))
            {
                try
                {
                    restored.Add(DownloadItem.FromSaved(item));
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Couldn't restore the saved Deezer download {0} - {1}", item.Artist, item.Title);
                }
            }

            return restored;
        }

        internal static bool ShouldRestore(SavedDownload saved, DateTime utcNow, Func<string, bool> folderHasAudio)
        {
            if (string.IsNullOrWhiteSpace(saved?.Id) || string.IsNullOrWhiteSpace(saved.DeezerUrl) || saved.DeezerAlbumId == 0)
                return false;

            switch (saved.Status)
            {
                case DownloadItemStatus.Queued:
                case DownloadItemStatus.Downloading:
                    return true;

                // Lidarr moves the files out when it imports, so an empty or missing folder means the album is done.
                // One that still has audio wasn't imported yet, and reporting it again lets Lidarr import it.
                case DownloadItemStatus.Completed:
                    return !string.IsNullOrWhiteSpace(saved.DownloadFolder) && folderHasAudio(saved.DownloadFolder);

                // kept so Lidarr still blocklists the release and searches again
                case DownloadItemStatus.Failed:
                case DownloadItemStatus.Warning:
                    return utcNow - saved.StatusChangedAt < FailedRetention;

                default:
                    return false;
            }
        }

        private static bool FolderHasAudio(string folder)
        {
            try
            {
                return Directory.Exists(folder) &&
                       Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                           .Any(f => AudioExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
