using System;
using System.Collections.Generic;
using System.Linq;
using DeezNET.Data;
using NzbDrone.Core.Download.Clients.Deezer.Queue;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Plugin.Deezer.HealthChecks
{
    // With "Fall Back to Lower Quality" on, a FLAC grab can come back with some tracks as MP3. The download is otherwise
    // fine, so it completes and Lidarr imports each file at its real quality, but nothing said the album was mixed.
    // Lidarr's download queue shows a client message on a completed item as "Import failed", so the notice goes here.
    [CheckOn(typeof(DeezerQualityFallbackEvent))]
    [CheckOn(typeof(AlbumImportedEvent))]
    public class QualityFallbackCheck : HealthCheckBase
    {
        private const int MaxListed = 5;
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

        private readonly IQualityFallbackTracker _tracker;
        private readonly IMediaFileService _mediaFileService;

        public QualityFallbackCheck(IQualityFallbackTracker tracker, IMediaFileService mediaFileService, ILocalizationService localizationService)
            : base(localizationService)
        {
            _tracker = tracker;
            _mediaFileService = mediaFileService;
        }

        public override HealthCheck Check()
        {
            foreach (var fallback in _tracker.All())
            {
                // an upgrade, from Deezer or anywhere else, replaces the lower-quality files and the notice has done its job
                if (DateTime.UtcNow - fallback.RecordedAt > MaxAge ||
                    IsResolved(_mediaFileService.GetFilesByAlbum(fallback.AlbumId).Select(f => f.Quality?.Quality), fallback.Requested))
                {
                    _tracker.Remove(fallback.AlbumId);
                }
            }

            var fallbacks = _tracker.All();
            if (fallbacks.Count == 0)
            {
                return new HealthCheck(GetType());
            }

            // only HealthCheck(Type) and the setters exist in every Lidarr version; see DuplicateInstallCheck
            return new HealthCheck(GetType())
            {
                Type = HealthCheckResult.Notice,
                Message = BuildMessage(fallbacks)
            };
        }

        internal static string BuildMessage(IReadOnlyList<QualityFallback> fallbacks)
        {
            var listed = fallbacks.Take(MaxListed).Select(f =>
                $"{f.Artist} - {f.Title} ({string.Join(", ", f.LowerTracks.OrderByDescending(t => TrackVerifier.Rank(t.Key)).Select(t => $"{t.Value} as {DownloadItem.GetQualityLabel(t.Key)}"))} " +
                $"of {f.TrackCount} tracks, grabbed as {DownloadItem.GetQualityLabel(f.Requested)})");

            var message = $"{(fallbacks.Count == 1 ? "A Deezer download" : $"{fallbacks.Count} Deezer downloads")} came back partly below the grabbed quality " +
                          "because Fall Back to Lower Quality is on. Lidarr imports those tracks at their real quality and can upgrade them " +
                          "if the quality profile allows. " + string.Join("; ", listed);

            if (fallbacks.Count > MaxListed)
            {
                message += $"; and {fallbacks.Count - MaxListed} more";
            }

            return message;
        }

        // Resolved once the album has files and none is below the grabbed quality. Files not yet imported keep the notice.
        internal static bool IsResolved(IEnumerable<Quality> fileQualities, Bitrate requested)
        {
            var qualities = fileQualities.ToList();
            var minimum = Weight(ToLidarrQuality(requested));

            return qualities.Count > 0 && qualities.All(q => Weight(q) >= minimum);
        }

        private static Quality ToLidarrQuality(Bitrate bitrate) => bitrate switch
        {
            Bitrate.FLAC => Quality.FLAC,
            Bitrate.MP3_320 => Quality.MP3_320,
            _ => Quality.MP3_128
        };

        // the default weights rank lossless above MP3-320 above MP3-128, which is all this needs
        private static int Weight(Quality quality) =>
            quality == null ? 0 : Quality.DefaultQualityDefinitions.FirstOrDefault(d => quality.Equals(d.Quality))?.Weight ?? 0;
    }
}
