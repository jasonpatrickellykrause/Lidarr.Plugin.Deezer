using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;
using NzbDrone.Plugin.Deezer;

namespace NzbDrone.Core.Indexers.Deezer
{
    public class DeezerIndexerSettingsValidator : AbstractValidator<DeezerIndexerSettings>
    {
    }

    public class DeezerIndexerSettings : IIndexerSettings
    {
        private static readonly DeezerIndexerSettingsValidator Validator = new DeezerIndexerSettingsValidator();

        // Password privacy makes Lidarr mask the ARL in the UI and API, and keep the stored value when the mask is saved back
        [FieldDefinition(0, Label = "Arl", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Arl { get; set; } = "";

        // Lidarr can only mask a whole field, so this shows the last four characters to compare a saved ARL with a new one.
        // The setter ignores input; the value always comes from Arl.
        [FieldDefinition(3, Label = "Saved Arl", HelpText = "Last four characters of the saved ARL, for comparing it with a new one. Editing this field has no effect.", Type = FieldType.Textbox, Hidden = HiddenType.HiddenIfNotSet)]
        public string ArlHint
        {
            get => ARLUtilities.GetHint(Arl);
            set { }
        }

        [FieldDefinition(1, Label = "Hide Albums With Missing Tracks", HelpText = "If an album has any unavailable tracks on Deezer, they will not be provided when searching.", Type = FieldType.Checkbox)]
        public bool HideAlbumsWithMissing { get; set; } = true;

        [FieldDefinition(2, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        // this is hardcoded so this doesn't need to exist except that it's required by the interface
        public string BaseUrl { get; set; } = "";

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
