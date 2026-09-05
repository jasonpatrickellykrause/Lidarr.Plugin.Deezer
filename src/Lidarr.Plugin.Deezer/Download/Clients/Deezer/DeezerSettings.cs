using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.Deezer
{
    public class DeezerSettingsValidator : AbstractValidator<DeezerSettings>
    {
        public DeezerSettingsValidator()
        {
            RuleFor(x => x.DownloadPath).IsValidPath();
        }
    }

    public class DeezerSettings : IProviderConfig
    {
        private static readonly DeezerSettingsValidator Validator = new DeezerSettingsValidator();

        [FieldDefinition(0, Label = "Download Path", Type = FieldType.Textbox)]
        public string DownloadPath { get; set; } = "";

        [FieldDefinition(1, Label = "Save Synced Lyrics", HelpText = "Saves synced lyrics to a separate .lrc file if available. Requires .lrc to be allowed under Import Extra Files.", Type = FieldType.Checkbox)]
        public bool SaveSyncedLyrics { get; set; } = false;

        [FieldDefinition(2, Label = "Use LRCLIB as Backup Lyric Provider", HelpText = "If Deezer does not have plain or synced lyrics for a track, the plugin will attempt to get them from LRCLIB.", Type = FieldType.Checkbox)]
        public bool UseLRCLIB { get; set; } = false;

        [FieldDefinition(3, Label = "Download Delay", Unit = "ms", HelpText = "Minimum delay between track downloads. Deezer has been known to invalidate ARLs that are used to make requests in rapid succession; a small delay makes the traffic pattern look less automated. A random amount of jitter (up to half this value) is added on top.", Type = FieldType.Number, Advanced = true)]
        public int DownloadDelay { get; set; } = 1500;

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
