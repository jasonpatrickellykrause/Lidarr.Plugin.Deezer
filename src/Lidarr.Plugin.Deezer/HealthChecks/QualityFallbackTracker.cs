using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DeezNET.Data;
using NzbDrone.Common.Messaging;

namespace NzbDrone.Plugin.Deezer.HealthChecks
{
    // A completed Deezer download where some tracks were only available below the grabbed quality and "Fall Back to Lower
    // Quality" downloaded them anyway. LowerTracks counts those tracks by the quality they came in.
    public record QualityFallback(int AlbumId, string Artist, string Title, Bitrate Requested, int TrackCount, IReadOnlyDictionary<Bitrate, int> LowerTracks)
    {
        public DateTime RecordedAt { get; init; } = DateTime.UtcNow;

        public int LowerTrackCount => LowerTracks.Values.Sum();
    }

    public interface IQualityFallbackTracker
    {
        void Record(QualityFallback fallback);
        IReadOnlyList<QualityFallback> All();
        void Remove(int albumId);
    }

    // Kept in memory only, like MissingEditionTracker. Keyed by Lidarr album, so grabbing the album again replaces the entry.
    public class QualityFallbackTracker : IQualityFallbackTracker
    {
        private readonly ConcurrentDictionary<int, QualityFallback> _fallbacks = new();

        public void Record(QualityFallback fallback) => _fallbacks[fallback.AlbumId] = fallback;

        public IReadOnlyList<QualityFallback> All() => _fallbacks.Values.OrderBy(f => f.RecordedAt).ToList();

        public void Remove(int albumId) => _fallbacks.TryRemove(albumId, out _);
    }

    // Published when a download completes with lower-quality tracks, so QualityFallbackCheck runs right away.
    public class DeezerQualityFallbackEvent : IEvent
    {
    }
}
