namespace NzbDrone.Plugin.Deezer
{
    public static class ARLUtilities
    {
        public static bool IsValid(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            try
            {
                // calling this gets a checkForm/API token, it will always return one regardless of the arl being valid or not, requiring the additional checks
                DeezerAPI.Instance.Client.SetARL(token).Wait();

                // a rejected ARL still gets user data back, but for an anonymous session with USER_ID 0
                var user = DeezerAPI.Instance.Client.GWApi.ActiveUserData!["USER"]!;
                bool loggedIn = user.Value<long>("USER_ID") != 0;
                bool hasStreaming = user["OPTIONS"]!.Value<bool>("web_streaming");
                if (!loggedIn || !hasStreaming)
                    return false;
            }
            catch
            {
                return false;
            }


            return true;
        }

        /// <summary>
        /// Returns the last four characters of an ARL, so a saved ARL can be compared with a new one without exposing it.
        /// </summary>
        public static string GetHint(string arl)
        {
            if (string.IsNullOrWhiteSpace(arl))
                return "";

            var trimmed = arl.Trim();
            return trimmed.Length <= 4 ? "****" : "****" + trimmed[^4..];
        }
    }
}
