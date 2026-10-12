using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Deezer;

namespace NzbDrone.Core.Indexers.Deezer
{
    public class DeezerRequestGenerator : IIndexerRequestGenerator
    {
        private const int PageSize = 100;
        private const int MaxPages = 30;
        public DeezerIndexerSettings Settings { get; set; }
        public Logger Logger { get; set; }

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.AddTier(GetRequests($"never gonna give you up"));

            // TODO: this seems to cause problems in some cases, but I have yet to debug further, the above is a basic workaround which should work fine
            /*Dictionary<string, string> data = new()
            {
                { "gateway_input", new JObject()
                    {
                        ["PAGE"] = "channels/explore",
                        ["VERSION"] = "2.3",
                        ["SUPPORT"] = new JObject()
                        {
                            ["grid"] = new JArray()
                            {
                                "channel",
                                "album"
                            },
                            ["horizontal-grid"] = new JArray()
                            {
                                "album"
                            }
                        },
                        ["LANG"] = "us"
                    }.ToString(Newtonsoft.Json.Formatting.None)
                }
            };

            var url = DeezerAPI.Instance!.GetGWUrl("page.get", data);
            var req = new IndexerRequest(url, HttpAccept.Json);
            req.HttpRequest.Method = System.Net.Http.HttpMethod.Post;
            req.HttpRequest.Cookies.Add("sid", DeezerAPI.Instance.Client.SID);

            pageableRequests.Add(new[]
            {
                req
            });*/

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            var minTrackCount = GetMinTrackCount(searchCriteria);

            chain.AddTier(GetRequests($"artist:\"{searchCriteria.ArtistQuery}\" album:\"{searchCriteria.AlbumQuery}\"", searchCriteria.ArtistQuery, searchCriteria.AlbumTitle, minTrackCount));
            chain.AddTier(GetRequests($"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}", searchCriteria.ArtistQuery, searchCriteria.AlbumTitle, minTrackCount, isFallback: true));

            return chain;
        }

        // the shortest MusicBrainz release of the searched album, so the parser can skip the title-track single Deezer
        // lists under the same name; null when Lidarr is searching several albums or has no release data
        private int? GetMinTrackCount(AlbumSearchCriteria searchCriteria)
        {
            try
            {
                if (searchCriteria.Albums is not { Count: 1 })
                    return null;

                var counts = searchCriteria.Albums[0].AlbumReleases?.Value?
                    .Select(r => r.TrackCount)
                    .Where(c => c > 0)
                    .ToList();

                return counts is { Count: > 0 } ? counts.Min() : null;
            }
            catch (Exception ex)
            {
                Logger?.Debug(ex, "Couldn't read the release track counts for {0}", searchCriteria);
                return null;
            }
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            chain.AddTier(GetRequests($"artist:\"{searchCriteria.ArtistQuery}\"", searchCriteria.ArtistQuery));
            chain.AddTier(GetRequests(searchCriteria.ArtistQuery, searchCriteria.ArtistQuery, isFallback: true));

            return chain;
        }

        // searchedArtist, searchedAlbum and minTrackCount let the parser drop unrelated results before it looks up their
        // album pages
        private IEnumerable<IndexerRequest> GetRequests(string searchParameters, string searchedArtist = null, string searchedAlbum = null, int? minTrackCount = null, bool isFallback = false)
        {
            DeezerAPI.Instance?.TryUpdateToken();

            for (var page = 0; page < MaxPages; page++)
            {
                JObject data = new()
                {
                    ["query"] = searchParameters,
                    ["start"] = $"{page * PageSize}",
                    ["nb"] = $"{PageSize}",
                    ["output"] = "ALBUM",
                    ["filter"] = "ALL",
                };

                var url = DeezerAPI.Instance!.GetGWUrl("search.music");
                var req = new DeezerSearchRequest(url, HttpAccept.Json, searchedArtist, searchedAlbum, minTrackCount, isFallback);
                req.HttpRequest.SetContent(data.ToString(Newtonsoft.Json.Formatting.None));
                req.HttpRequest.Method = System.Net.Http.HttpMethod.Post;
                req.HttpRequest.Cookies.Add("sid", DeezerAPI.Instance.Client.SID);
                yield return req;
            }
        }
    }
}
