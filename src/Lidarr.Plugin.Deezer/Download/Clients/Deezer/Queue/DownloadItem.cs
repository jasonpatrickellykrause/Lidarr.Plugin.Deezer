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

        // set when the download completed with some tracks below the grabbed quality
        public QualityFallback QualityFallback { get; private set; }

        public float Progress { get => DownloadedSize / (float)Math.Max(TotalSize, 1); }
        public long DownloadedSize { get; private set; }
        public long TotalSize { get; private set; }

        public int FailedTracks { get; private set; }

        // tracks that downloaded but failed TrackVerifier; counted in FailedTracks too
        public int VerificationFailures { get; private set; }

        private (long id, long size)[] _tracks;
        private string[] _recordingIds;
        private DeezerURL _deezerUrl;
        private JToken _deezerAlbum;
        private DateTime _lastARLValidityCheck = DateTime.MinValue;

        // tracks that came in below the grabbed quality, by the quality they came in
        private readonly Dictionary<Bitrate, int> _lowerTracks = new();

        // file names (without extension) given out in this download, and the track files written, for the album check
        private readonly HashSet<string> _claimedFileNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _writtenFiles = new();

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

                        await DoTrackDownload(trackId, settings, logger, cancellation);
                        DownloadedSize += trackSize;
                    }
                    // only a real cancellation is ignored; HttpClient reports its own timeout as a TaskCanceledException,
                    // and swallowing that would mark the album Completed with the track missing
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
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
                Message = GetFailureMessage(FailedTracks, VerificationFailures, _tracks.Length);
            }
            else if (!cancellation.IsCancellationRequested && GetMissingFilesMessage(_writtenFiles, _tracks.Length, File.Exists) is { } missingFiles)
            {
                Status = DownloadItemStatus.Failed;
                Message = missingFiles;
                logger.Error("{0}: {1}", Title, missingFiles);
            }
            else
            {
                Status = DownloadItemStatus.Completed;
                QualityFallback = BuildQualityFallback(RemoteAlbum?.Albums?.FirstOrDefault()?.Id ?? 0, Artist, Title, Bitrate, _tracks.Length, _lowerTracks);
            }
        }

        // Every track reported success, but Lidarr imports from the folder, so check the files are all there. This catches a
        // track overwriting another and anything that removed a file during the download.
        internal static string GetMissingFilesMessage(IReadOnlyCollection<string> writtenFiles, int trackCount, Func<string, bool> exists)
        {
            var present = writtenFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count(exists);
            if (present >= trackCount)
                return null;

            return $"All {trackCount} tracks downloaded, but the download folder has {present} of the {trackCount} track files, so Lidarr would import an incomplete album.";
        }

        // Track numbers restart on each disc, so "%track% - %title%" can name two tracks the same, such as an "Intro" on
        // two discs. The second used to overwrite the first, and Lidarr failed the import with a track missing. Only the
        // later track is renamed, so single-disc albums keep their names. Compared without case, because Windows and macOS
        // file systems treat names that differ only in case as the same file.
        internal static string ClaimFileName(ISet<string> claimed, string name, int disc)
        {
            if (claimed.Add(name))
                return name;

            var withDisc = $"{disc}-{name}";
            if (claimed.Add(withDisc))
                return withDisc;

            for (var n = 2; ; n++)
            {
                var numbered = $"{withDisc} ({n})";
                if (claimed.Add(numbered))
                    return numbered;
            }
        }

        internal static int ParseDisc(JToken diskNumber) =>
            int.TryParse(diskNumber?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var disc) && disc > 0 ? disc : 1;

        // only for a completed download; a failed one is blocklisted and searched again, so its quality doesn't matter
        internal static QualityFallback BuildQualityFallback(int albumId, string artist, string title, Bitrate requested, int trackCount, IReadOnlyDictionary<Bitrate, int> lowerTracks)
        {
            if (albumId == 0 || lowerTracks.Count == 0)
                return null;

            return new QualityFallback(albumId, artist, title, requested, trackCount, new Dictionary<Bitrate, int>(lowerTracks));
        }

        private async Task DoTrackDownload(long track, DeezerSettings settings, Logger logger, CancellationToken cancellation = default)
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
            var outDir = Path.Combine(settings.DownloadPath, MetadataUtilities.GetFilledTemplate(albumFolder, ext, page, _deezerAlbum));
            var fileName = ClaimFileName(_claimedFileNames, MetadataUtilities.GetFilledTemplate("%track% - %title%", ext, page, _deezerAlbum), ParseDisc(page["DATA"]?["DISK_NUMBER"]));
            var outPath = Path.Combine(outDir, $"{fileName}.{ext}");
            outDir = Path.GetDirectoryName(outPath)!;

            DownloadFolder = outDir;
            if (!Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            try
            {
                outPath = await WriteTrack(track, outPath, settings, cancellation);
                var verified = TrackVerifier.Verify(outPath, duration, Bitrate, settings.FallbackToLowerBitrate);

                // a 128 grab can fall back up to 320; only a drop is worth reporting
                if (TrackVerifier.Rank(verified.Bitrate) < TrackVerifier.Rank(Bitrate))
                {
                    _lowerTracks[verified.Bitrate] = _lowerTracks.GetValueOrDefault(verified.Bitrate) + 1;
                    logger.Info("Deezer track {0} in {1} was only available as {2}, below the grabbed {3}", track, Title, GetQualityLabel(verified.Bitrate), GetQualityLabel(Bitrate));
                }
            }
            catch (TrackVerificationException)
            {
                DeletePartialFile(outPath);
                VerificationFailures++;
                throw;
            }
            catch
            {
                // a half-written or empty file would otherwise be picked up by a later import of this folder
                DeletePartialFile(outPath);
                throw;
            }

            var plainLyrics = string.Empty;
            List<SyncLyrics> syncLyrics = null;

            var lyrics = await TryFetchLyrics(() => DeezerAPI.Instance.Client.Downloader.FetchLyricsFromDeezer(track, cancellation), "Deezer", track, logger, cancellation);
            if (lyrics.HasValue)
            {
                plainLyrics = lyrics.Value.plainLyrics;

                if (settings.SaveSyncedLyrics)
                    syncLyrics = lyrics.Value.syncLyrics;
            }

            if (settings.UseLRCLIB && (string.IsNullOrWhiteSpace(plainLyrics) || (settings.SaveSyncedLyrics && !HasSyncedLines(syncLyrics))))
            {
                lyrics = await TryFetchLyrics(() => DeezerAPI.Instance.Client.Downloader.FetchLyricsFromLRCLIB("lrclib.net", songTitle, artistName, albumTitle, duration, cancellation), "LRCLIB", track, logger, cancellation);
                if (lyrics.HasValue)
                {
                    if (string.IsNullOrWhiteSpace(plainLyrics))
                        plainLyrics = lyrics.Value.plainLyrics;
                    if (settings.SaveSyncedLyrics && !HasSyncedLines(syncLyrics))
                        syncLyrics = lyrics.Value.syncLyrics;
                }
            }

            await DeezerAPI.Instance.Client.Downloader.ApplyMetadataToFile(track, outPath, 512, plainLyrics, GetMusicBrainzIdsForTrack(track), cancellation);
            _writtenFiles.Add(outPath);

            // an empty list, or one with only blank lines, would write an empty .lrc that Lidarr imports next to the track
            if (HasSyncedLines(syncLyrics))
                await CreateLrcFile(Path.Combine(outDir, $"{fileName}.lrc"), syncLyrics);

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

        internal static string GetFailureMessage(int failedTracks, int verificationFailures, int totalTracks)
        {
            var message = $"{failedTracks} of {totalTracks} tracks failed to download.";
            if (verificationFailures > 0)
                message += $" {verificationFailures} downloaded but failed the audio check (unreadable, wrong format, or shorter than Deezer lists, such as a 30-second preview).";

            return message + " The Lidarr log has the error for each track.";
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

        internal static string GetQualityLabel(Bitrate bitrate) => bitrate switch
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

        // Lyrics are optional extras fetched after the audio is written. An error from Deezer's lyrics endpoint or from
        // LRCLIB (a third-party service) used to fail the track, so one dropped connection got a complete album marked
        // failed and blocklisted. A rejected ARL and cancellation still propagate.
        private static async Task<(string plainLyrics, List<SyncLyrics> syncLyrics)?> TryFetchLyrics(
            Func<Task<(string plainLyrics, List<SyncLyrics> syncLyrics)?>> fetch,
            string source,
            long track,
            Logger logger,
            CancellationToken cancellation)
        {
            try
            {
                return await fetch();
            }
            catch (InvalidARLException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.Warn("Couldn't fetch lyrics for Deezer track {0} from {1}, saving it without them: {2}", track, source, ex.Message);
                return null;
            }
        }

        // the lines CreateLrcFile writes; Deezer's synced lyrics include entries with no text for instrumental breaks
        private static bool IsWritableLine(SyncLyrics lyric) => !string.IsNullOrEmpty(lyric.LrcTimestamp) && !string.IsNullOrEmpty(lyric.Line);

        internal static bool HasSyncedLines(List<SyncLyrics> syncLyrics) => syncLyrics?.Any(IsWritableLine) ?? false;

        private static async Task CreateLrcFile(string lrcFilePath, List<SyncLyrics> syncLyrics)
        {
            StringBuilder lrcContent = new();
            foreach (var lyric in syncLyrics.Where(IsWritableLine))
                lrcContent.AppendLine(CultureInfo.InvariantCulture, $"{lyric.LrcTimestamp} {lyric.Line}");

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

                // under half the tracks of the shortest release isn't another edition, it's a different release grabbed
                // under the album's name, usually the title-track single. Adding it to MusicBrainz as an edition would
                // be wrong, so don't suggest it. Same rule as DeezerResultFilter.HasEnoughTracks.
                var shortestRelease = releases.Where(r => r.TrackCount > 0).Select(r => r.TrackCount).DefaultIfEmpty(0).Min();
                if (_tracks.Length * 2 < shortestRelease)
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
