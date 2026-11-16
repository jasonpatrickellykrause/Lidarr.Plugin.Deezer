using NzbDrone.Plugin.Deezer;
using Shouldly;

namespace Lidarr.Plugin.Deezer.Test
{
    public class ARLUtilitiesTests
    {
        [Theory]
        [InlineData(null, "")]
        [InlineData("   ", "")]
        [InlineData("abc", "****")]
        [InlineData("abcd", "****")]
        [InlineData("abcdefgh", "****efgh")]
        [InlineData("  abcdefgh  ", "****efgh")]
        public void GetHintShowsOnlyTheLastFourCharacters(string arl, string expected)
        {
            ARLUtilities.GetHint(arl).ShouldBe(expected);
        }
    }
}
