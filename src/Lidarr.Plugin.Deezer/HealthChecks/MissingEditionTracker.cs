using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Messaging;

namespace NzbDrone.Plugin.Deezer.HealthChecks
{
    // A Deezer album grabbed for a Lidarr album that has no MusicBrainz release with the same number of tracks.
    public record MissingEdition(int AlbumId, string Artist, string Title, long DeezerAlbumId, int DeezerTrackCount, int[] ReleaseTrackCounts)
    {
        public DateTime RecordedAt { get; init; } = DateTime.UtcNow;

        public string DeezerUrl => $"https://www.deezer.com/album/{DeezerAlbumId}";

        // Harmony looks up a streaming release and fills in a MusicBrainz release editor submission from it
        public string HarmonyUrl => $"https://harmony.pulsewidth.org.uk/release?url={Uri.EscapeDataString(DeezerUrl)}";
    }

    public interface IMissingEditionTracker
    {
        void Record(MissingEdition edition);
        IReadOnlyList<MissingEdition> All();
        void Remove(long deezerAlbumId);
    }

    // Kept in memory only, so the list starts empty after a restart. Each grab of an affected album adds it again.
    public class MissingEditionTracker : IMissingEditionTracker
    {
        private readonly ConcurrentDictionary<long, MissingEdition> _editions = new();

        public void Record(MissingEdition edition) => _editions[edition.DeezerAlbumId] = edition;

        public IReadOnlyList<MissingEdition> All() => _editions.Values.OrderBy(e => e.RecordedAt).ToList();

        public void Remove(long deezerAlbumId) => _editions.TryRemove(deezerAlbumId, out _);
    }

    // Published when a grab records a missing edition, so MissingEditionCheck runs right away.
    public class DeezerEditionMissingEvent : IEvent
    {
    }
}
