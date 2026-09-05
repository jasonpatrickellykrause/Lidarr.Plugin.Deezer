using System;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Deezer;
using NzbDrone.Core.Parser;
using NzbDrone.Plugin.Deezer;

namespace NzbDrone.Core.Indexers.Deezer
{
    public class Deezer : HttpIndexerBase<DeezerIndexerSettings>
    {
        public override string Name => "Deezer";
        public override string Protocol => nameof(DeezerDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 100;
        // search requests can page up to 30 times per tier with no other pacing; avoid hammering Deezer's endpoint
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(1);

        private readonly IDeezerProxy _deezerProxy;

        public Deezer(IDeezerProxy deezerProxy,
            IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _deezerProxy = deezerProxy;
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            // note: Firehawk no longer provides up-to-date Deezer tokens so this has no use anymore.
            /*if (string.IsNullOrEmpty(Settings.Arl))
            {
                var arlTask = ARLUtilities.GetFirstValidARL();
                arlTask.Wait();
                Settings.Arl = arlTask.Result;
            }*/

            DeezerAPI.Instance?.CheckAndSetARL(Settings.Arl);

            return new DeezerRequestGenerator()
            {
                Settings = Settings,
                Logger = _logger
            };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new DeezerParser()
            {
                Settings = Settings
            };
        }
    }
}
