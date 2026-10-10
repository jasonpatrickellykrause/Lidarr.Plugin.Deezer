using System;
using System.Collections.Generic;
using System.Linq;
using DeezNET;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Deezer;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Plugins;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Plugin.Deezer.HealthChecks
{
    // A dead ARL otherwise only shows up as failed searches and downloads. Reporting it here puts it on System > Status
    // and sends it to any notification set up for health issues.
    [CheckOn(typeof(DeezerArlRejectedEvent))]
    [CheckOn(typeof(ProviderAddedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderUpdatedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderDeletedEvent<IIndexer>))]
    public class ArlCheck : HealthCheckBase
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly IIndexerFactory _indexerFactory;
        private readonly Logger _logger;

        public ArlCheck(IIndexerFactory indexerFactory, ILocalizationService localizationService, Logger logger)
            : base(localizationService)
        {
            _indexerFactory = indexerFactory;
            _logger = logger;
        }

        public override HealthCheck Check()
        {
            // the download client has no ARL of its own; it downloads with the one the indexer set
            var arls = _indexerFactory.All()
                .Where(d => d.Enable && d.Settings is DeezerIndexerSettings settings && !string.IsNullOrWhiteSpace(settings.Arl))
                .GroupBy(d => ((DeezerIndexerSettings)d.Settings).Arl.Trim())
                .ToList();

            var errors = new List<string>();
            var warnings = new List<string>();

            foreach (var arl in arls)
            {
                var names = string.Join(", ", arl.Select(d => d.Name));
                var hint = ARLUtilities.GetHint(arl.Key);

                switch (CheckArl(arl.Key))
                {
                    case ArlState.Rejected:
                        errors.Add($"Deezer no longer accepts the ARL ending in {hint}, used by {names}. Paste a new ARL into the indexer settings.");
                        break;
                    case ArlState.NoStreaming:
                        errors.Add($"The Deezer account for the ARL ending in {hint}, used by {names}, has no streaming subscription, so downloads will fail.");
                        break;
                    case ArlState.Unreachable:
                        warnings.Add($"Couldn't reach Deezer to check the ARL ending in {hint}, used by {names}. The Lidarr log has the error.");
                        break;
                }
            }

            if (errors.Count == 0 && warnings.Count == 0)
            {
                return new HealthCheck(GetType());
            }

            // only HealthCheck(Type) and the setters exist in every Lidarr version; see DuplicateInstallCheck
            return new HealthCheck(GetType())
            {
                Type = errors.Count > 0 ? HealthCheckResult.Error : HealthCheckResult.Warning,
                Message = string.Join(" ", errors.Concat(warnings)),
                WikiUrl = new HttpUri($"{new DeezerPlugin().GithubUrl}#installation")
            };
        }

        private ArlState CheckArl(string arl)
        {
            try
            {
                // a separate client, so checking never swaps the token out from under a running search or download
                var client = new DeezerClient();
                if (!client.SetARL(arl).Wait(Timeout))
                {
                    _logger.Warn("Timed out checking the Deezer ARL ending in {0}", ARLUtilities.GetHint(arl));
                    return ArlState.Unreachable;
                }

                // a rejected ARL still gets user data back, but for an anonymous session with USER_ID 0
                var user = client.GWApi.ActiveUserData?["USER"];
                if (user == null || user.Value<long>("USER_ID") == 0)
                {
                    return ArlState.Rejected;
                }

                return user["OPTIONS"]?.Value<bool>("web_streaming") == true ? ArlState.Valid : ArlState.NoStreaming;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Couldn't check the Deezer ARL ending in {0}", ARLUtilities.GetHint(arl));
                return ArlState.Unreachable;
            }
        }

        private enum ArlState
        {
            Valid,
            Rejected,
            NoStreaming,
            Unreachable
        }
    }
}
