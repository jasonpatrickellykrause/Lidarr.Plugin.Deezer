using NzbDrone.Common.Messaging;

namespace NzbDrone.Plugin.Deezer.HealthChecks
{
    // Published when Deezer rejects the ARL during a search or download, so ArlCheck runs right away instead of at the
    // next scheduled health check.
    public class DeezerArlRejectedEvent : IEvent
    {
    }
}
