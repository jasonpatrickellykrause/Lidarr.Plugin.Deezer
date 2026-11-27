using System;
using System.Collections.Generic;
using System.IO;
using NzbDrone.Plugin.Deezer;
using Shouldly;

namespace Lidarr.Plugin.Deezer.Test
{
    public class StaleInstallCleanupTests : IDisposable
    {
        private static readonly Version Running = new(10, 2, 0, 100);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "deezer-plugins-test-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, Version> _versions = new(StringComparer.OrdinalIgnoreCase);

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        private string Install(string owner, Version version)
        {
            var folder = Path.Combine(_root, owner, StaleInstallCleanup.PluginFolderName);
            Directory.CreateDirectory(folder);
            var dll = Path.Combine(folder, StaleInstallCleanup.AssemblyFileName);
            File.WriteAllText(dll, "");
            _versions[dll] = version;
            return folder;
        }

        private List<string> Find(string owner = "matrix-runout", Version running = null) =>
            StaleInstallCleanup.FindStaleCopies(_root, owner, running ?? Running, path => _versions.TryGetValue(path, out var v) ? v : null);

        [Fact]
        public void Finds_an_older_fork_build_under_the_previous_owner()
        {
            Install("matrix-runout", Running);
            var old = Install("previous-owner", new Version(10, 2, 0, 96));

            Find().ShouldBe(new[] { old });
        }

        [Fact]
        public void Leaves_TrevTVs_original_plugin_alone()
        {
            Install("matrix-runout", Running);
            Install("TrevTV", new Version(10, 1, 0, 18));

            Find().ShouldBeEmpty();
        }

        [Fact]
        public void Leaves_a_newer_build_under_another_owner_alone()
        {
            Install("matrix-runout", Running);
            Install("someone-else", new Version(10, 2, 0, 200));

            Find().ShouldBeEmpty();
        }

        [Fact]
        public void Does_nothing_when_the_running_copy_is_not_installed_under_its_own_owner()
        {
            // a local build, or a copy installed from somewhere unexpected, must not delete the only working install
            Install("previous-owner", new Version(10, 2, 0, 96));

            Find().ShouldBeEmpty();
        }

        [Fact]
        public void Never_returns_the_running_copys_own_folder()
        {
            Install("matrix-runout", Running);

            Find().ShouldBeEmpty();
        }

        [Fact]
        public void Ignores_other_plugins_and_owner_folders_without_this_plugin()
        {
            Install("matrix-runout", Running);
            Directory.CreateDirectory(Path.Combine(_root, "previous-owner", "Lidarr.Plugin.Slskd"));
            Directory.CreateDirectory(Path.Combine(_root, "empty-owner"));

            Find().ShouldBeEmpty();
        }

        [Fact]
        public void Skips_a_copy_whose_version_cannot_be_read()
        {
            Install("matrix-runout", Running);
            var unreadable = Install("previous-owner", new Version(10, 2, 0, 96));
            _versions.Remove(Path.Combine(unreadable, StaleInstallCleanup.AssemblyFileName));

            Find().ShouldBeEmpty();
        }

        [Fact]
        public void Returns_nothing_when_the_plugin_folder_does_not_exist()
        {
            StaleInstallCleanup.FindStaleCopies(Path.Combine(_root, "missing"), "matrix-runout", Running).ShouldBeEmpty();
        }
    }
}
