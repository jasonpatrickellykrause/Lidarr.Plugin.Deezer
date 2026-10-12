using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers.Deezer
{
    // A search.music page request that remembers what it was searching for. Lidarr hands this same object back on the
    // IndexerResponse, so the parser can filter results even for queries that don't name the artist or album.
    public class DeezerSearchRequest : IndexerRequest
    {
        public DeezerSearchRequest(string url, HttpAccept httpAccept, string searchedArtist, string searchedAlbum, int? minTrackCount, bool isFallback)
            : base(url, httpAccept)
        {
            SearchedArtist = searchedArtist;
            SearchedAlbum = searchedAlbum;
            MinTrackCount = minTrackCount;
            IsFallback = isFallback;
        }

        public string SearchedArtist { get; }

        // null for artist searches
        public string SearchedAlbum { get; }

        // track count of the album's shortest MusicBrainz release; null when unknown or for artist searches
        public int? MinTrackCount { get; }

        // the last tier; it never comes back empty just because nothing matched the filters
        public bool IsFallback { get; }
    }
}
