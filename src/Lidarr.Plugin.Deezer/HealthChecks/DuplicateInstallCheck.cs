using System;
using System.IO;
using System.Linq;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Plugins;

namespace NzbDrone.Plugin.Deezer.HealthChecks
{
    // Two copies of this plugin (for example TrevTV's and a fork) define the same indexer and download client,
    // which makes Lidarr fail with "Sequence contains more than one matching element". Name the folders so the
    // user knows exactly what to remove.
    public class DuplicateInstallCheck : HealthCheckBase
    {
        private const string AssemblyFileName = "Lidarr.Plugin.Deezer.dll";

        private readonly IAppFolderInfo _appFolderInfo;

        public DuplicateInstallCheck(IAppFolderInfo appFolderInfo, ILocalizationService localizationService)
            : base(localizationService)
        {
            _appFolderInfo = appFolderInfo;
        }

        public override HealthCheck Check()
        {
            var folders = _appFolderInfo.GetPluginAssemblies()
                .Where(path => string.Equals(Path.GetFileName(path), AssemblyFileName, StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetDirectoryName)
                .Distinct()
                .ToList();

            if (folders.Count <= 1)
            {
                return new HealthCheck(GetType());
            }

            var message = $"The Deezer plugin is installed {folders.Count} times, which breaks indexers and download clients. " +
                          $"Stop Lidarr, delete all but one of these folders, then start Lidarr: {string.Join(", ", folders)}";

            return new HealthCheck(GetType(), HealthCheckResult.Error, message)
            {
                WikiUrl = new HttpUri($"{new DeezerPlugin().GithubUrl}#switching-from-another-deezer-plugin")
            };
        }
    }
}
