using System;
using System.IO;
using DeezNET.Data;
using NzbDrone.Core.Download.Clients.Deezer.Queue;
using Shouldly;

namespace Lidarr.Plugin.Deezer.Test
{
    // fixtures are 3-second 440 Hz sine waves made with ffmpeg
    public class TrackVerifierTests : IDisposable
    {
        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "deezer-verifier-" + Guid.NewGuid().ToString("N"));

        public TrackVerifierTests()
        {
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            Directory.Delete(_tempDir, true);
        }

        private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);

        // copied so a test can give the file a misleading extension, like a fallback MP3 still named .flac
        private string CopyFixture(string name, string asName)
        {
            var path = Path.Combine(_tempDir, asName);
            File.Copy(Fixture(name), path);
            return path;
        }

        private string WriteBytes(string name, byte[] bytes)
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        [Fact]
        public void FlacPasses()
        {
            var result = TrackVerifier.Verify(Fixture("sine-3s.flac"), 3, Bitrate.FLAC, false);

            result.IsFlac.ShouldBeTrue();
            result.Duration.TotalSeconds.ShouldBe(3, 0.1);
        }

        [Theory]
        [InlineData("sine-3s.mp3")]
        [InlineData("sine-3s-noid3.mp3")]
        public void Mp3Passes(string fixture)
        {
            var result = TrackVerifier.Verify(Fixture(fixture), 3, Bitrate.MP3_320, false);

            result.IsFlac.ShouldBeFalse();
            result.Duration.TotalSeconds.ShouldBe(3, 0.2);
        }

        [Theory]
        [InlineData("sine-3s.flac", Bitrate.FLAC)]
        [InlineData("sine-3s-320.mp3", Bitrate.MP3_320)]
        [InlineData("sine-3s.mp3", Bitrate.MP3_128)]
        [InlineData("sine-3s-noid3.mp3", Bitrate.MP3_128)]
        public void ReportsTheQualityTheFileActuallyIs(string fixture, Bitrate expected)
        {
            // requested FLAC with fallback on, so every fixture is allowed and only the detection is tested
            TrackVerifier.Verify(Fixture(fixture), 3, Bitrate.FLAC, true).Bitrate.ShouldBe(expected);
        }

        [Theory]
        [InlineData(0, Bitrate.MP3_128)]
        [InlineData(128, Bitrate.MP3_128)]
        [InlineData(223, Bitrate.MP3_128)]
        [InlineData(224, Bitrate.MP3_320)]
        [InlineData(320, Bitrate.MP3_320)]
        public void Mp3BitrateThreshold(int kbps, Bitrate expected)
        {
            TrackVerifier.GetMp3Bitrate(kbps).ShouldBe(expected);
        }

        [Fact]
        public void Mp3NamedFlacIsReadAsMp3()
        {
            // with fallback on, the file is verified before anything else looks at its extension
            var path = CopyFixture("sine-3s.mp3", "track.flac");

            TrackVerifier.Verify(path, 3, Bitrate.FLAC, true).IsFlac.ShouldBeFalse();
        }

        [Fact]
        public void Mp3ForFlacGrabFailsWithoutFallback()
        {
            var path = CopyFixture("sine-3s.mp3", "track.flac");

            Should.Throw<TrackVerificationException>(() => TrackVerifier.Verify(path, 3, Bitrate.FLAC, false))
                .Message.ShouldContain("MP3 for a FLAC grab");
        }

        [Fact]
        public void NoiseFails()
        {
            // what a wrong decryption key produces
            var bytes = new byte[8192];
            new Random(1).NextBytes(bytes);
            bytes[0] = 0x12;
            var path = WriteBytes("track.flac", bytes);

            Should.Throw<TrackVerificationException>(() => TrackVerifier.Verify(path, 3, Bitrate.FLAC, false))
                .Message.ShouldContain("not FLAC or MP3");
        }

        [Fact]
        public void EmptyFileFails()
        {
            var path = WriteBytes("track.mp3", Array.Empty<byte>());

            Should.Throw<TrackVerificationException>(() => TrackVerifier.Verify(path, 3, Bitrate.MP3_320, false))
                .Message.ShouldContain("empty");
        }

        [Fact]
        public void FlacHeaderWithoutAudioFails()
        {
            var path = WriteBytes("track.flac", "fLaC"u8.ToArray());

            Should.Throw<TrackVerificationException>(() => TrackVerifier.Verify(path, 3, Bitrate.FLAC, false));
        }

        [Fact]
        public void MuchShorterThanDeezerFails()
        {
            Should.Throw<TrackVerificationException>(() => TrackVerifier.Verify(Fixture("sine-3s.flac"), 200, Bitrate.FLAC, false))
                .Message.ShouldContain("incomplete");
        }

        [Fact]
        public void ShortTracksGetSlack()
        {
            // 3 seconds against 5 is under 90%, but within the 2-second allowance for rounding and padding
            TrackVerifier.Verify(Fixture("sine-3s.flac"), 5, Bitrate.FLAC, false).IsFlac.ShouldBeTrue();
        }

        [Fact]
        public void UnknownDurationSkipsTheCheck()
        {
            TrackVerifier.Verify(Fixture("sine-3s.flac"), 0, Bitrate.FLAC, false).IsFlac.ShouldBeTrue();
        }

        [Fact]
        public void LongerThanDeezerPasses()
        {
            TrackVerifier.Verify(Fixture("sine-3s.flac"), 2, Bitrate.FLAC, false).IsFlac.ShouldBeTrue();
        }
    }
}
