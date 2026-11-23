using System;
using System.IO;
using DeezNET.Data;

namespace NzbDrone.Core.Download.Clients.Deezer.Queue
{
    public class TrackVerificationException : Exception
    {
        public TrackVerificationException(string message)
            : base(message)
        {
        }

        public TrackVerificationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public record VerifiedTrack(bool IsFlac, TimeSpan Duration);

    // Checks a decrypted track before it is tagged. A track that finishes downloading can still be unusable: a decryption
    // problem writes noise, and Deezer sometimes serves a 30-second preview in place of the full track. Both used to be
    // reported as Completed, and Lidarr imported them or failed later with a less useful error.
    internal static class TrackVerifier
    {
        // a file this much shorter than Deezer's duration is a preview or an incomplete track
        private const double MinimumDurationRatio = 0.9;

        // Deezer rounds durations to whole seconds, and encoder padding varies, so very short tracks need some slack
        private static readonly TimeSpan DurationSlack = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan PreviewLength = TimeSpan.FromSeconds(30);

        public static VerifiedTrack Verify(string path, int expectedSeconds, Bitrate requested, bool fallbackAllowed)
        {
            var isFlac = DetectFormat(path);

            if (requested == Bitrate.FLAC && !isFlac && !fallbackAllowed)
                throw new TrackVerificationException($"Deezer returned an MP3 for a FLAC grab of {Path.GetFileName(path)}.");

            var duration = ReadDuration(path, isFlac);
            if (duration <= TimeSpan.Zero)
                throw new TrackVerificationException($"{Path.GetFileName(path)} has no audio.");

            CheckDuration(path, duration, expectedSeconds);

            return new VerifiedTrack(isFlac, duration);
        }

        internal static bool DetectFormat(string path)
        {
            var header = new byte[4];
            int read;
            using (var stream = File.OpenRead(path))
                read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

            if (read < header.Length)
                throw new TrackVerificationException($"{Path.GetFileName(path)} is empty or too short to be audio ({read} bytes).");

            if (header[0] == 'f' && header[1] == 'L' && header[2] == 'a' && header[3] == 'C')
                return true;

            // an ID3v2 tag, or the 11-bit MPEG frame sync
            if ((header[0] == 'I' && header[1] == 'D' && header[2] == '3') || (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0))
                return false;

            throw new TrackVerificationException($"{Path.GetFileName(path)} is not FLAC or MP3 audio (starts with {Convert.ToHexString(header)}). The track may not have decrypted correctly.");
        }

        private static TimeSpan ReadDuration(string path, bool isFlac)
        {
            try
            {
                // the MIME type, not the extension, picks the parser; with fallback the extension can still be wrong here
                using var file = TagLib.File.Create(path, isFlac ? "taglib/flac" : "taglib/mp3", TagLib.ReadStyle.Average);
                return file.Properties?.Duration ?? TimeSpan.Zero;
            }
            catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
            {
                throw new TrackVerificationException($"{Path.GetFileName(path)} could not be read as {(isFlac ? "FLAC" : "MP3")}: {ex.Message}", ex);
            }
        }

        private static void CheckDuration(string path, TimeSpan actual, int expectedSeconds)
        {
            // Deezer leaves the duration out for some tracks; there is nothing to compare against
            if (expectedSeconds <= 0)
                return;

            var expected = TimeSpan.FromSeconds(expectedSeconds);
            if (actual >= expected * MinimumDurationRatio || expected - actual <= DurationSlack)
                return;

            var isPreview = (actual - PreviewLength).Duration() <= TimeSpan.FromSeconds(1);
            var reason = isPreview ? "Deezer likely served a 30-second preview" : "the track is incomplete";

            throw new TrackVerificationException($"{Path.GetFileName(path)} is {actual.TotalSeconds:0} seconds long but Deezer lists it at {expectedSeconds} seconds; {reason}.");
        }
    }
}
