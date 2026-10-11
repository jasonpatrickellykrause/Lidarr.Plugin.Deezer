using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers.Deezer
{
    // A search.music page request that remembers which artist it was for. Lidarr hands this same object back on the
    // IndexerResponse, so the parser can filter results by artist even for queries that don't name the artist.
    public class DeezerSearchRequest : IndexerRequest
    {
        public DeezerSearchRequest(string url, HttpAccept httpAccept, string searchedArtist, bool isFallback)
            : base(url, httpAccept)
        {
            SearchedArtist = searchedArtist;
            IsFallback = isFallback;
        }

        public string SearchedArtist { get; }

        // the last tier; if none of its results are credited to the artist they are kept rather than returning nothing
        public bool IsFallback { get; }
    }
}
