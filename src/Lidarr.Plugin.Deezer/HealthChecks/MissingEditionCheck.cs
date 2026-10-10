using System;
using System.Linq;
using NzbDrone.Common.Http;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.Events;

namespace NzbDrone.Plugin.Deezer.HealthChecks
{
    // Lidarr only knows releases that are in MusicBrainz. When Deezer has an edition with a track count no release of the
    // album shares, Lidarr can't match the files on import. This lists those albums with a link to add them to MusicBrainz.
    [CheckOn(typeof(DeezerEditionMissingEvent))]
    [CheckOn(typeof(ArtistRefreshCompleteEvent))]
    public class MissingEditionCheck : HealthCheckBase
    {
        private const int MaxListed = 5;
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

        private readonly IMissingEditionTracker _tracker;
        private readonly IReleaseService _releaseService;

        public MissingEditionCheck(IMissingEditionTracker tracker, IReleaseService releaseService, ILocalizationService localizationService)
            : base(localizationService)
        {
            _tracker = tracker;
            _releaseService = releaseService;
        }

        public override HealthCheck Check()
        {
            foreach (var edition in _tracker.All())
            {
                // once someone adds the edition to MusicBrainz and Lidarr refreshes the artist, a release with the same
                // track count shows up and the warning has done its job
                if (DateTime.UtcNow - edition.RecordedAt > MaxAge ||
                    _releaseService.GetReleasesByAlbum(edition.AlbumId).Any(r => r.TrackCount == edition.DeezerTrackCount))
                {
                    _tracker.Remove(edition.DeezerAlbumId);
                }
            }

            var editions = _tracker.All();
            if (editions.Count == 0)
            {
                return new HealthCheck(GetType());
            }

            var listed = editions.Take(MaxListed).Select(e =>
                $"{e.Artist} - {e.Title} (Deezer has {e.DeezerTrackCount} tracks, MusicBrainz has {string.Join(" or ", e.ReleaseTrackCounts.Distinct().OrderBy(c => c))}): {e.HarmonyUrl}");

            var message = $"{(editions.Count == 1 ? "A Deezer edition isn't" : $"{editions.Count} Deezer editions aren't")} in MusicBrainz, so Lidarr " +
                          $"may not match the files on import. Add them to MusicBrainz with Harmony, then refresh the artist. " +
                          string.Join("; ", listed);

            if (editions.Count > MaxListed)
            {
                message += $"; and {editions.Count - MaxListed} more";
            }

            // only HealthCheck(Type) and the setters exist in every Lidarr version; see DuplicateInstallCheck
            return new HealthCheck(GetType())
            {
                Type = HealthCheckResult.Warning,
                Message = message,
                WikiUrl = new HttpUri("https://harmony.pulsewidth.org.uk/")
            };
        }
    }
}
