using System.Linq;
using System.Reflection;

namespace NzbDrone.Core.Plugins
{
    public class DeezerPlugin : Plugin
    {
        // used when the build doesn't pass a repository, such as a local build
        private const string DefaultRepository = "jasonpatrickellykrause/Lidarr.Plugin.Deezer";

        // Lidarr installs into plugins/<owner>/<repo> from the URL the user enters, but uninstalls and checks for
        // updates using GithubUrl. CI stamps the repository it builds from so the two always match, including in forks.
        private static readonly string Repository = typeof(DeezerPlugin).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PluginRepository" && !string.IsNullOrWhiteSpace(a.Value))?.Value ?? DefaultRepository;

        public override string Name => "Deezer";
        public override string Owner => Repository.Split('/')[0];
        public override string GithubUrl => $"https://github.com/{Repository}";
    }
}
