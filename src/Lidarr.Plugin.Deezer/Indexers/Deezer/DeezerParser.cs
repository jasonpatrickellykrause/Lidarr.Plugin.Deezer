using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DeezNET.Exceptions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients.Deezer;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Parser.Model;
using System.Collections.Concurrent;
using NzbDrone.Plugin.Deezer;
using System.Globalization;

namespace NzbDrone.Core.Indexers.Deezer
{
    public class DeezerParser : IParseIndexerResponse
    {
        public DeezerIndexerSettings Settings { get; set; }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse response)
        {
            var torrentInfos = new List<ReleaseInfo>();

            DeezerSearchResponse jsonResponse = null;
            if (response.HttpRequest.Url.FullUri.Contains("method=page.get", StringComparison.InvariantCulture)) // means we're asking for a channel and need to parse it accordingly
            {
                var task = GenerateSearchResponseFromChannelData(response.Content);
                task.Wait();
                jsonResponse = task.Result;
            }
            else
                jsonResponse = ParseSearchResponse(response);

            if (jsonResponse?.Data == null)
                return torrentInfos;

            var tasks = jsonResponse.Data.Select(result => ProcessResultAsync(result)).ToArray();

            Task.WaitAll(tasks);

            foreach (var task in tasks)
            {
                if (task.Result != null)
                    torrentInfos.AddRange(task.Result);
            }
            
            return torrentInfos
                .OrderByDescending(o => o.Size)
                .ToArray();
        }

        // A gw api error comes back as HTTP 200 with empty results, so it has to be checked here or it surfaces as a
        // null reference while parsing
        private static DeezerSearchResponse ParseSearchResponse(IndexerResponse response)
        {
            var json = JObject.Parse(response.Content);
            var error = json["error"];

            if (error == null || !error.HasValues)
                return json["results"]?.ToObject<DeezerSearchResponse>();

            if (error["VALID_TOKEN_REQUIRED"] != null || error["GATEWAY_ERROR"] != null)
                return SearchWithFreshToken(response);

            throw new IndexerException(response, "Deezer returned an error: " + error.ToString(Formatting.None));
        }

        // The request was built with a token Deezer no longer accepts. Refresh it and run the same page again through
        // DeezNET, which retries once more on its own before giving up on the ARL.
        private static DeezerSearchResponse SearchWithFreshToken(IndexerResponse response)
        {
            var body = JObject.Parse(Encoding.UTF8.GetString(response.HttpRequest.ContentData));
            var query = body["query"]!.Value<string>();
            var type = body["output"]!.Value<string>();
            var start = body["start"]!.Value<int>();
            var count = body["nb"]!.Value<int>();

            try
            {
                DeezerAPI.Instance.RefreshToken();
                var results = DeezerRequestPacer.RunAsync(() => DeezerAPI.Instance.Client.GWApi.SearchMusic(query, type, start, count))
                    .GetAwaiter().GetResult();

                return results.ToObject<DeezerSearchResponse>();
            }
            catch (InvalidARLException ex)
            {
                throw new IndexerException(response, "Deezer rejected the session even after refreshing the API token. The ARL has probably expired; paste a new one into the Deezer indexer and download client settings. " + ex.Message);
            }
        }

        private async Task<IList<ReleaseInfo>> ProcessResultAsync(DeezerGwAlbum result)
        {
            var torrentInfos = new List<ReleaseInfo>();

            var albumId = long.Parse(result.AlbumId, CultureInfo.InvariantCulture);
            var albumPage = await DeezerRequestPacer.RunAsync(() => DeezerAPI.Instance.Client.GWApi.GetAlbumPage(albumId));

            var missing = albumPage["SONGS"]!["data"]!.Count(d => !IsAvailable(d));
            if (Settings.HideAlbumsWithMissing && missing > 0)
                return null; // return null if missing any tracks

            var size128 = albumPage["SONGS"]!["data"]!.Sum(d => d["FILESIZE_MP3_128"]!.Value<long>());
            var size320 = albumPage["SONGS"]!["data"]!.Sum(d => d["FILESIZE_MP3_320"]!.Value<long>());
            var sizeFlac = albumPage["SONGS"]!["data"]!.Sum(d => d["FILESIZE_FLAC"]!.Value<long>());

            // the account allowing a format doesn't mean the album has it; only offer a format every available track has,
            // otherwise the grab fails partway through with NoSourcesAvailableException
            var availableSongs = albumPage["SONGS"]!["data"]!.Where(IsAvailable).ToList();
            var all320 = availableSongs.All(d => d["FILESIZE_MP3_320"]!.Value<long>() > 0);
            var allFlac = availableSongs.All(d => d["FILESIZE_FLAC"]!.Value<long>() > 0);

            // MP3 128
            torrentInfos.Add(ToReleaseInfo(result, 1, size128));

            // MP3 320
            if (all320 && DeezerAPI.Instance.Client.GWApi.ActiveUserData["USER"]!["OPTIONS"]!["web_hq"]!.Value<bool>())
            {
                torrentInfos.Add(ToReleaseInfo(result, 3, size320));
            }

            // FLAC
            if (allFlac && DeezerAPI.Instance.Client.GWApi.ActiveUserData["USER"]!["OPTIONS"]!["web_lossless"]!.Value<bool>())
            {
                torrentInfos.Add(ToReleaseInfo(result, 9, sizeFlac));
            }

            return torrentInfos;
        }

        // Deezer can list a file size for a track it won't serve. Those tracks have an empty RIGHTS record, show as greyed out
        // in the app, and fail to download with error 2002. A track without a RIGHTS field counts as available, so a response
        // that leaves the field out changes nothing.
        private static bool IsAvailable(JToken song)
        {
            if (song["FILESIZE"]?.ToString() == "0")
                return false;

            return song["RIGHTS"] switch
            {
                JObject rights => rights.Properties().Any(p => p.Name.EndsWith("_AVAILABLE", StringComparison.Ordinal)
                                                              && p.Value.Type == JTokenType.Boolean
                                                              && p.Value.Value<bool>()),
                JArray rights => rights.Count > 0,
                _ => true
            };
        }

        private static ReleaseInfo ToReleaseInfo(DeezerGwAlbum x, int bitrate, long size)
        {
            var publishDate = DateTime.UtcNow;
            var year = 0;
            if (DateTime.TryParse(x.DigitalReleaseDate, out var digitalReleaseDate))
            {
                publishDate = digitalReleaseDate;
                year = publishDate.Year;
            }
            else if (DateTime.TryParse(x.PhysicalReleaseDate, out var physicalReleaseDate))
            {
                publishDate = physicalReleaseDate;
                year = publishDate.Year;
            }

            var url = $"https://deezer.com/album/{x.AlbumId}";

            var result = new ReleaseInfo
            {
                Guid = $"Deezer-{x.AlbumId}-{bitrate}",
                Artist = x.ArtistName,
                Album = x.AlbumTitle,
                DownloadUrl = url,
                InfoUrl = url,
                PublishDate = publishDate,
                DownloadProtocol = nameof(DeezerDownloadProtocol)
            };

            string format;
            switch (bitrate)
            {
                case 9:
                    result.Codec = "FLAC";
                    result.Container = "Lossless";
                    format = "FLAC";
                    break;
                case 3:
                    result.Codec = "MP3";
                    result.Container = "320";
                    format = "MP3 320";
                    break;
                case 1:
                    result.Codec = "MP3";
                    result.Container = "128";
                    format = "MP3 128";
                    break;
                default:
                    throw new NotImplementedException();
            }

            result.Size = size;
            result.Title = $"{x.ArtistName} - {x.AlbumTitle}";

            if (year > 0)
            {
                result.Title += $" ({year})";
            }

            if (x.Explicit)
            {
                result.Title += " [Explicit]";
            }

            result.Title += $" [{format}] [WEB]";

            return result;
        }

        // based on the code for the /api/newReleases endpoint of deemix
        private async Task<DeezerSearchResponse> GenerateSearchResponseFromChannelData(string channelData)
        {
            var page = JObject.Parse(channelData)["results"]!;
            var musicSection = page["sections"]!.First(s => s["section_id"]!.ToString().Contains("module_id=83718b7b-5503-4062-b8b9-3530e2e2cefa"));
            var channels = musicSection["items"]!.Select(i => i["target"]!.ToString()).ToArray();

            var newReleasesByChannel = await Task.WhenAll(channels.Select(c => GetChannelNewReleases(c)));

            var seen = new ConcurrentDictionary<long, bool>();
            var distinct = new ConcurrentBag<JToken>();

            Parallel.ForEach(newReleasesByChannel.SelectMany(l => l), r =>
            {
                var id = r["ALB_ID"]!.Value<long>();
                if (seen.TryAdd(id, true))
                {
                    distinct.Add(r);
                }
            });

            var sortedDistinct = distinct.OrderByDescending(a => DateTime.TryParse(a["DIGITAL_RELEASE_DATE"]!.Value<string>()!, out var release) ? release : DateTime.MinValue).ToList();

            var now = DateTime.Now;
            var recent = sortedDistinct.Where(a => DateTime.TryParse(a["DIGITAL_RELEASE_DATE"]!.Value<string>()!, out var release) && (now - release).Days < 8);

            JObject baseObj = new();
            JArray dataArray = new();

            long albumCount = 0;

            await Task.WhenAll(recent.Select(async album =>
            {
                var id = album["ALB_ID"]!.Value<long>();
                var result = await DeezerRequestPacer.RunAsync(() => DeezerAPI.Instance.Client.GWApi.GetAlbumPage(id));

                var duration = result["SONGS"]!.Sum(track => track.Contains("DURATION") ? track["DURATION"]!.Value<long>() : 0L);
                var trackCount = result["SONGS"]!.Count();

                var data = result["DATA"]!;
                data["DURATION"] = duration;
                data["NUMBER_TRACK"] = trackCount;
                data["LINK"] = $"https://deezer.com/album/{id}";

                lock (dataArray)
                {
                    dataArray.Add(data);
                    albumCount++;
                }
            }));

            baseObj.Add("data", dataArray);
            baseObj.Add("total", albumCount);

            return baseObj.ToObject<DeezerSearchResponse>();
        }

        private async Task<JToken[]> GetChannelNewReleases(string channelName)
        {
            var channelData = await DeezerRequestPacer.RunAsync(() => DeezerAPI.Instance.Client.GWApi.GetPage(channelName));
            Regex regex = new("New.*releases");

            var newReleasesSection = (JObject)channelData["sections"]!.FirstOrDefault(s => regex.IsMatch(s["title"]!.ToString()))!;
            if (newReleasesSection == null)
                return Array.Empty<JToken>();

            if (newReleasesSection.ContainsKey("target"))
            {
                var showAll = await DeezerRequestPacer.RunAsync(() => DeezerAPI.Instance.Client.GWApi.GetPage(newReleasesSection["target"]!.ToString()));
                return showAll["sections"]!.First()!["items"]!.Select(i => i["data"]!).ToArray();
            }

            return newReleasesSection.ContainsKey("items")
                ? newReleasesSection["items"]!.Select(i => i["data"]!).ToArray()
                : Array.Empty<JToken>();
        }
    }
}
