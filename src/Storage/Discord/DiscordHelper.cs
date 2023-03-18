using Discord;

namespace DiscordFS.Storage.Discord;

public static class DiscordHelper
{
    public static RequestOptions CreateDefaultOptions(CancellationToken cancellationToken)
    {
        return new RequestOptions
        {
            CancelToken = cancellationToken,
            RetryMode = RetryMode.AlwaysRetry
        };
    }
}