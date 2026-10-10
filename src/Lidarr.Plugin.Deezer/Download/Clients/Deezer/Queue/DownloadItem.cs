using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezNET;
using DeezNET.Data;
using DeezNET.Exceptions;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Deezer;
using NzbDrone.Plugin.Deezer.HealthChecks;

namespace NzbDrone.Core.Download.Clients.Deezer.Queue
{
    public class DownloadItem
    {
        public MusicBrainzIds MusicBrainzIds { get; private set; }

        public static async Task<DownloadItem> From(RemoteAlbum remoteAlbum)
        {
            string url = remoteAlbum.Release.DownloadUrl.Trim();
            Bitrate bitrate;

            if (remoteAlbum.Release.Codec == "FLAC")
                bitrate = Bitrate.FLAC;
            else if (remoteAlbum.Release.Container == "320")
                bitrate = Bitrate.MP3_320;
            else
                bitrate = Bitrate.MP3_128;

            DownloadItem item = null;
            if (DeezerURL.TryParse(url, out var deezerUrl))
            {
                item = new()
                {
                    ID = Guid.NewGuid().ToString(),
                    Status = DownloadItemStatus.Queued,
                    Bitrate = bitrate,
                    RemoteAlbum = remoteAlbum,
                    _deezerUrl = deezerUrl,
                };

                item.SetMusicBrainzIds(remoteAlbum);
                await item.SetDeezerData();
                item.DetectMissingEdition(remoteAlbum);
            }

            return item;
        }

        public string ID { get; private set; }

        public string Title { get; private set; }
        public string Artist { get; private set; }
        public bool Explicit { get; private set; }

        public RemoteAlbum RemoteAlbum {  get; private set; }

        public string DownloadFolder { get; private set; }

        public Bitrate Bitrate { get; private set; }
        public DownloadItemStatus Status { get; set; }

        // shown in Lidarr's Activity queue next to a failed or warning item
        public string Message { get; set; }

        public bool ArlRejected { get; private set; }

        // set when no MusicBrainz release of the album has as many tracks as Deezer's edition
        public MissingEdition MissingEdition { get; private set; }

        public float Progress { get => DownloadedSize / (float)Math.Max(TotalSize, 1); }
        public long DownloadedSize { get; private set; }
        public long TotalSize { get; private set; }

        public int FailedTracks { get; private set; }

        private (long id, long size)[] _tracks;
        private string[] _recordingIds;
        private DeezerURL _deezerUrl;
        private JToken _deezerAlbum;
        private DateTime _lastARLValidityCheck = DateTime.MinValue;

        public async Task DoDownload(DeezerSettings settings, Logger logger, CancellationToken cancellation = default)
        {
            // check the ARL before the first track request rather than alongside it
            EnsureValidity();

            List<Task> tasks = new();
            using SemaphoreSlim semaphore = new(1, 1);
            var isFirstTrack = true;
            var random = new Random();
            InvalidARLException arlError = null;
            foreach (var (trackId, trackSize) in _tracks)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellation);
                    try
                    {
                        // once the ARL is rejected every remaining track fails the same way, and each extra request risks getting it flagged
                        if (arlError != null)
                        {
                            FailedTracks++;
                            return;
                        }

                        // pace requests to Deezer; hammering it back to back is a known way to get an ARL invalidated
                        if (!isFirstTrack && settings.DownloadDelay > 0)
                        {
                            var jitter = random.Next(0, settings.DownloadDelay / 2 + 1);
                            await Task.Delay(settings.DownloadDelay + jitter, cancellation);
                        }
                        isFirstTrack = false;

                        await DoTrackDownload(trackId, settings, cancellation);
                        DownloadedSize += trackSize;
                    }
                    catch (TaskCanceledException) { }
                    catch (InvalidARLException ex)
                    {
                        arlError = ex;
                        logger.Error($"Deezer rejected the ARL while downloading track {trackId}. Skipping the remaining tracks in {Title}.");
                        logger.Error(ex.ToString());
                        FailedTracks++;
                    }
                    catch (Exception ex)
                    {
                        logger.Error("Error while downloading Deezer track " + trackId);
                        logger.Error(ex.ToString());
                        FailedTracks++;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellation));
            }

            await Task.WhenAll(tasks);

            if (arlError != null)
            {
                // not the release's fault, so don't let Lidarr blocklist it; a warning keeps it visible until the ARL is replaced
                ArlRejected = true;
                Status = DownloadItemStatus.Warning;
                Message = $"Deezer rejected the ARL after {_tracks.Length - FailedTracks} of {_tracks.Length} tracks. Replace the ARL in the Deezer indexer, then remove this item and search again.";
            }
            else if (FailedTracks > 0)
            {
                Status = DownloadItemStatus.Failed;
                Message = $"{FailedTracks} of {_tracks.Length} tracks failed to download. The Lidarr log has the error for each track.";
            }
            else
            {
                Status = DownloadItemStatus.Completed;
            }
        }

        private async Task DoTrackDownload(long track, DeezerSettings settings, CancellationToken cancellation = default)
        {
            var page = await DeezerAPI.Instance.Client.GWApi.GetTrackPage(track, cancellation);

            var songTitle = page["DATA"]!["SNG_TITLE"]!.ToString();
            var songVersion = page["DATA"]?["VERSION"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(songVersion))
                songTitle = $"{songTitle} {songVersion}";

            var albumTitle = page["DATA"]!["ALB_TITLE"]!.ToString();
            var albumVersion = _deezerAlbum["DATA"]?["VERSION"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(albumVersion))
                albumTitle = $"{albumTitle} {albumVersion}";

            var artistName = page["DATA"]!["ART_NAME"]!.ToString();
            var duration = page["DATA"]!["DURATION"]!.Value<int>();

            var ext = Bitrate == Bitrate.FLAC ? "flac" : "mp3";
            // one folder per requested quality, so a failed FLAC attempt never shares a folder with a later MP3 grab of the same album
            var albumFolder = $"%albumartist%/%album% [{GetQualityLabel(Bitrate)}]/";
            var outPath = Path.Combine(settings.DownloadPath, MetadataUtilities.GetFilledTemplate(albumFolder, ext, page, _deezerAlbum), MetadataUtilities.GetFilledTemplate("%track% - %title%.%ext%", ext, page, _deezerAlbum));
            var outDir = Path.GetDirectoryName(outPath)!;

            DownloadFolder = outDir;
            if (!Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            try
            {
                outPath = await WriteTrack(track, outPath, settings, cancellation);
            }
            catch
            {
                // a half-written or empty file would otherwise be picked up by a later import of this folder
                DeletePartialFile(outPath);
                throw;
            }

            var plainLyrics = string.Empty;
            List<SyncLyrics> syncLyrics = null;

            var lyrics = await DeezerAPI.Instance.Client.Downloader.FetchLyricsFromDeezer(track, cancellation);
            if (lyrics.HasValue)
            {
                plainLyrics = lyrics.Value.plainLyrics;

                if (settings.SaveSyncedLyrics)
                    syncLyrics = lyrics.Value.syncLyrics;
            }

            if (settings.UseLRCLIB && (string.IsNullOrWhiteSpace(plainLyrics) || (settings.SaveSyncedLyrics && !(syncLyrics?.Any() ?? false))))
            {
                lyrics = await DeezerAPI.Instance.Client.Downloader.FetchLyricsFromLRCLIB("lrclib.net", songTitle, artistName, albumTitle, duration, cancellation);
                if (lyrics.HasValue)
                {
                    if (string.IsNullOrWhiteSpace(plainLyrics))
                        plainLyrics = lyrics.Value.plainLyrics;
                    if (settings.SaveSyncedLyrics && !(syncLyrics?.Any() ?? false))
                        syncLyrics = lyrics.Value.syncLyrics;
                }
            }

            await DeezerAPI.Instance.Client.Downloader.ApplyMetadataToFile(track, outPath, 512, plainLyrics, GetMusicBrainzIdsForTrack(track), cancellation);

            if (syncLyrics != null)
                await CreateLrcFile(Path.Combine(outDir, MetadataUtilities.GetFilledTemplate("%track% - %title%.%ext%", "lrc", page, _deezerAlbum)), syncLyrics);

            // TODO: this is currently a waste of resources, if this pr ever gets merged, it can be reenabled
            // https://github.com/Lidarr/Lidarr/pull/4370
            /* try
            {
                string artOut = Path.Combine(outDir, "folder.jpg");
                if (!File.Exists(artOut))
                {
                    byte[] bigArt = await DeezerAPI.Instance.Client.Downloader.GetArtBytes(page["DATA"]!["ALB_PICTURE"]!.ToString(), 1024, cancellation);
                    await File.WriteAllBytesAsync(artOut, bigArt, cancellation);
                }
            }
            catch (UnavailableArtException) { } */
        }

        private async Task<string> WriteTrack(long track, string outPath, DeezerSettings settings, CancellationToken cancellation)
        {
            Bitrate? fallback = settings.FallbackToLowerBitrate ? Downloader.GetLowerFallbackBitrate(Bitrate) : null;
            await DeezerAPI.Instance.Client.Downloader.WriteRawTrackToFile(track, outPath, Bitrate, fallback, cancellation);

            // the path was built for the requested bitrate, but a fallback may have returned a different format
            if (fallback != null)
                outPath = CorrectExtension(outPath);

            return outPath;
        }

        private static void DeletePartialFile(string path)
        {
            // a fallback may already have renamed the file to the other extension
            foreach (var candidate in new[] { path, Path.ChangeExtension(path, ".flac"), Path.ChangeExtension(path, ".mp3") }.Distinct())
            {
                try
                {
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string GetQualityLabel(Bitrate bitrate) => bitrate switch
        {
            Bitrate.FLAC => "FLAC",
            Bitrate.MP3_320 => "MP3 320",
            _ => "MP3 128"
        };

        public void EnsureValidity()
        {
            if ((DateTime.Now - _lastARLValidityCheck).TotalMinutes > 30)
            {
                _lastARLValidityCheck = DateTime.Now;
                var arlValid = ARLUtilities.IsValid(DeezerAPI.Instance.Client.ActiveARL);
                if (!arlValid)
                    throw new InvalidARLException("The applied ARL is not valid for downloading, cannot continue.");
            }
        }

        private async Task SetDeezerData(CancellationToken cancellation = default)
        {
            if (_deezerUrl.EntityType != EntityType.Album)
                throw new InvalidOperationException();

            var albumPage = await DeezerAPI.Instance.Client.GWApi.GetAlbumPage(_deezerUrl.Id, cancellation);

            var filesizeKey = Bitrate switch
            {
                Bitrate.MP3_128 => "FILESIZE_MP3_128",
                Bitrate.MP3_320 => "FILESIZE_MP3_320",
                Bitrate.FLAC => "FILESIZE_FLAC",
                _ => "FILESIZE"
            };

            _tracks ??= albumPage["SONGS"]!["data"]!.Select(t => (t["SNG_ID"]!.Value<long>(), t[filesizeKey]!.Value<long>())).ToArray();
            _deezerAlbum = albumPage;

            var album = albumPage["DATA"]!.ToObject<DeezerGwAlbum>();

            Title = album.AlbumTitle;
            Artist = album.ArtistName;
            Explicit = album.Explicit;
            TotalSize = _tracks.Sum(t => t.size);
        }

        private static string CorrectExtension(string path)
        {
            var magic = new byte[4];
            using (var stream = File.OpenRead(path))
                stream.ReadExactly(magic);

            var ext = magic.SequenceEqual("fLaC"u8.ToArray()) ? ".flac" : ".mp3";
            if (string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase))
                return path;

            var correctedPath = Path.ChangeExtension(path, ext);
            File.Move(path, correctedPath, true);
            return correctedPath;
        }

        private static async Task CreateLrcFile(string lrcFilePath, List<SyncLyrics> syncLyrics)
        {
            StringBuilder lrcContent = new();
            foreach (var lyric in syncLyrics)
            {
                if (!string.IsNullOrEmpty(lyric.LrcTimestamp) && !string.IsNullOrEmpty(lyric.Line))
                    lrcContent.AppendLine(CultureInfo.InvariantCulture, $"{lyric.LrcTimestamp} {lyric.Line}");
            }
            await File.WriteAllTextAsync(lrcFilePath, lrcContent.ToString());
        }

        private void SetMusicBrainzIds(RemoteAlbum remoteAlbum)
        {
            // an empty set (rather than null) tells DeezNET not to fall back to a blind MusicBrainz search,
            // since a guessed release ID would mislead Lidarr's import matching
            MusicBrainzIds = new MusicBrainzIds();

            try
            {
                if (remoteAlbum?.Artist == null || remoteAlbum.Albums == null || !remoteAlbum.Albums.Any())
                    return;

                var album = remoteAlbum.Albums[0];
                var monitoredRelease = album.AlbumReleases?.Value?.FirstOrDefault(r => r.Monitored);

                MusicBrainzIds = new MusicBrainzIds
                {
                    ArtistId = remoteAlbum.Artist.ForeignArtistId,
                    ReleaseGroupId = album.ForeignAlbumId,
                    ReleaseId = monitoredRelease?.ForeignReleaseId,
                    ReleaseArtistId = monitoredRelease != null ? album.Artist?.Value?.ForeignArtistId : null,
                };

                // ordered across discs so they line up with Deezer's album track order
                _recordingIds = monitoredRelease?.Tracks?.Value?
                    .OrderBy(t => t.AbsoluteTrackNumber)
                    .Select(t => t.ForeignRecordingId)
                    .ToArray();
            }
            catch (Exception)
            {
                // tagging is best-effort; never fail the grab over it
            }
        }

        private void DetectMissingEdition(RemoteAlbum remoteAlbum)
        {
            try
            {
                var album = remoteAlbum?.Albums?.FirstOrDefault();
                var releases = album?.AlbumReleases?.Value;
                if (album == null || releases == null || releases.Count == 0)
                    return;

                // another release with Deezer's track count means the edition is in MusicBrainz and Lidarr can match it
                if (releases.Any(r => r.TrackCount == _tracks.Length))
                    return;

                MissingEdition = new MissingEdition(
                    album.Id,
                    remoteAlbum.Artist?.Name ?? Artist,
                    album.Title ?? Title,
                    _deezerUrl.Id,
                    _tracks.Length,
                    releases.Select(r => r.TrackCount).ToArray());
            }
            catch (Exception)
            {
                // the warning is best-effort; never fail the grab over it
            }
        }

        private MusicBrainzIds GetMusicBrainzIdsForTrack(long trackId)
        {
            // when the monitored release has a different track count, Deezer has a different edition of the album
            if (_recordingIds != null && _recordingIds.Length != _tracks.Length)
            {
                // a release ID tag makes Lidarr's import consider only that release, which then fails with
                // "Has missing tracks"; leaving out both IDs lets Lidarr pick the edition that matches the files
                return MusicBrainzIds with { ReleaseId = null, ReleaseArtistId = null, RecordingId = null };
            }

            // the counts match, so tracks line up by position
            string recordingId = null;
            var index = Array.FindIndex(_tracks, t => t.id == trackId);
            if (_recordingIds != null && index >= 0)
                recordingId = _recordingIds[index];

            return MusicBrainzIds with { RecordingId = recordingId };
        }
    }
}
