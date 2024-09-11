using Microsoft.Extensions.Logging;

namespace OSCLeash.App;

internal static class Utilities
{
    public static void HandleBlazorError(this Task task, ILogger? logger = null)
    {
        task.ContinueWith(x => { logger?.LogError(x.Exception, "There was an error while processing."); }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
