using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Plugins;

namespace NzbDrone.Plugin.Deezer
{
    // Lidarr installs a plugin into plugins/<owner>/<repo>, taking the owner from the release URL, and loads every
    // plugin folder it finds. When this fork's repository moved to a new owner, the next update landed in a new
    // folder and left the previous install in place, so Lidarr loaded two copies. On startup, this removes copies
    // of this fork that sit under a different owner. Only this fork's builds (10.2 and later) no newer than the
    // running copy are removed, so TrevTV's original plugin (10.1.x) and unrelated plugins are never touched.
    public class StaleInstallCleanup : IHandle<ApplicationStartedEvent>
    {
        internal const string PluginFolderName = "Lidarr.Plugin.Deezer";
        internal const string AssemblyFileName = "Lidarr.Plugin.Deezer.dll";

        private readonly IAppFolderInfo _appFolderInfo;
        private readonly Logger _logger;

        public StaleInstallCleanup(IAppFolderInfo appFolderInfo, Logger logger)
        {
            _appFolderInfo = appFolderInfo;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            try
            {
                var pluginRoot = _appFolderInfo.GetPluginPath();
                var ownVersion = typeof(StaleInstallCleanup).Assembly.GetName().Version;
                var ownOwner = new DeezerPlugin().Owner;

                foreach (var folder in FindStaleCopies(pluginRoot, ownOwner, ownVersion))
                {
                    Directory.Delete(folder, true);
                    _logger.Warn("Removed an older copy of the Deezer plugin at {0}. It was left behind when the plugin's GitHub repository moved. Restart Lidarr to finish removing it.", folder);

                    var ownerFolder = Path.GetDirectoryName(folder);
                    if (ownerFolder != null && Directory.Exists(ownerFolder) && Directory.GetFileSystemEntries(ownerFolder).Length == 0)
                    {
                        Directory.Delete(ownerFolder);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Couldn't check for an older copy of the Deezer plugin. If System > Plugins lists Deezer twice, uninstall both and install from https://github.com/matrix-runout/Lidarr.Plugin.Deezer.");
            }
        }

        // Returns plugins/<other owner>/Lidarr.Plugin.Deezer folders that hold an older build of this fork. The running
        // copy's own folder is skipped, and so is the whole check unless the running copy is installed where expected.
        internal static List<string> FindStaleCopies(string pluginRoot, string ownOwner, Version ownVersion, Func<string, Version> readVersion = null)
        {
            readVersion ??= ReadVersion;

            var stale = new List<string>();

            if (string.IsNullOrWhiteSpace(pluginRoot) || !Directory.Exists(pluginRoot) || ownVersion == null)
            {
                return stale;
            }

            if (!File.Exists(Path.Combine(pluginRoot, ownOwner, PluginFolderName, AssemblyFileName)))
            {
                return stale;
            }

            foreach (var ownerFolder in Directory.GetDirectories(pluginRoot))
            {
                if (string.Equals(Path.GetFileName(ownerFolder), ownOwner, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidate = Path.Combine(ownerFolder, PluginFolderName);
                var assemblyPath = Path.Combine(candidate, AssemblyFileName);
                var version = File.Exists(assemblyPath) ? readVersion(assemblyPath) : null;

                if (version != null && IsSameForkLine(version, ownVersion) && version <= ownVersion)
                {
                    stale.Add(candidate);
                }
            }

            return stale;
        }

        // this fork's releases start at 10.2; TrevTV's original plugin is 10.1.x and below
        private static readonly Version FirstForkVersion = new(10, 2);

        private static bool IsSameForkLine(Version version, Version ownVersion)
        {
            return version >= FirstForkVersion && version.Major == ownVersion.Major;
        }

        private static Version ReadVersion(string assemblyPath)
        {
            if (!File.Exists(assemblyPath))
            {
                return null;
            }

            try
            {
                // reads the version from the file's metadata without loading the assembly
                return AssemblyName.GetAssemblyName(assemblyPath).Version;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
