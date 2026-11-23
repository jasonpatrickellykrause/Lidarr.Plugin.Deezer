using Moq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Deezer;
using Shouldly;

namespace Lidarr.Plugin.Deezer.Test
{
    public class DownloadClientTests
    {
        private const string DownloadPath = "/downloads/deezer";

        private readonly Mock<IDiskProvider> _diskProvider = new();

        private NzbDrone.Core.Download.Clients.Deezer.Deezer CreateClient()
        {
            var client = new NzbDrone.Core.Download.Clients.Deezer.Deezer(
                Mock.Of<IDeezerProxy>(),
                null,
                _diskProvider.Object,
                null,
                null,
                LogManager.CreateNullLogger());

            client.Definition = new DownloadClientDefinition
            {
                Name = "Deezer",
                Settings = new DeezerSettings { DownloadPath = DownloadPath },
            };

            return client;
        }

        [Fact]
        public void WritablePathPasses()
        {
            _diskProvider.Setup(d => d.FolderExists(DownloadPath)).Returns(true);
            _diskProvider.Setup(d => d.FolderWritable(DownloadPath)).Returns(true);

            CreateClient().Test().IsValid.ShouldBeTrue();
        }

        [Fact]
        public void MissingPathFails()
        {
            _diskProvider.Setup(d => d.FolderExists(DownloadPath)).Returns(false);

            var result = CreateClient().Test();

            result.IsValid.ShouldBeFalse();
            result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe(nameof(DeezerSettings.DownloadPath));
            result.Errors[0].ErrorMessage.ShouldBe("Folder does not exist");
        }

        [Fact]
        public void ReadOnlyPathFails()
        {
            _diskProvider.Setup(d => d.FolderExists(DownloadPath)).Returns(true);
            _diskProvider.Setup(d => d.FolderWritable(DownloadPath)).Returns(false);

            var result = CreateClient().Test();

            result.IsValid.ShouldBeFalse();
            result.Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe("Unable to write to folder");
        }
    }
}
