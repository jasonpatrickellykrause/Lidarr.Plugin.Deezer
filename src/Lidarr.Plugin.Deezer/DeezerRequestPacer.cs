using System;
using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Plugin.Deezer
{
    // Search results are followed by one album lookup per result. Sending a whole page of them at once is the kind of
    // burst that gets ARLs flagged, so every lookup goes through this shared gate: a couple at a time, each after a
    // short jittered pause. The gate is static so concurrent searches share it too.
    internal static class DeezerRequestPacer
    {
        private const int MaxConcurrentRequests = 2;
        private const int MinDelayMs = 250;

        private static readonly SemaphoreSlim Gate = new(MaxConcurrentRequests, MaxConcurrentRequests);

        public static async Task<T> RunAsync<T>(Func<Task<T>> request, CancellationToken token = default)
        {
            await Gate.WaitAsync(token);
            try
            {
                var jitter = Random.Shared.Next(0, (MinDelayMs / 2) + 1);
                await Task.Delay(MinDelayMs + jitter, token);
                return await request();
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
