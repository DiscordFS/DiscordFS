using Discord;
using Discord.WebSocket;
using DiscordFS.Storage.FileSystem;
using Microsoft.Extensions.Logging;

namespace DiscordFS.Storage.Discord;

public class DiscordStorageProvider : BaseStorageProvider<DiscordStorageProviderOptions>, IDiscordStorageProvider
{
    public static readonly Guid ProviderId = Guid.Parse(input: "605ed008-3aa9-49cb-935d-f8d935e24ede");
    public static readonly string ProviderVersion = "1.0";

    private readonly DiscordSocketClient _discordClient;
    private readonly ILogger<DiscordStorageProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public DiscordStorageProvider(
        ILogger<DiscordStorageProvider> logger,
        IHttpClientFactory httpClientFactory,
        IDiscordClient discordClient) : base(logger)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _discordClient = (DiscordSocketClient)discordClient;
    }

    protected override IRemoteFileSystemProvider CreateRemoteFileProvider()
    {
        return new DiscordRemoteFileSystemProvider(
            _logger,
            FileRangeManager,
            Options,
            _discordClient,
            _httpClientFactory);
    }

    protected override Task<string> GetInstanceIdAsync(DiscordStorageProviderOptions options)
    {
        return Task.FromResult(options.GuildId);
    }

    protected override Task<string> GetInstanceNameAsync(DiscordStorageProviderOptions options)
    {
        var guild = _discordClient.GetGuild(ulong.Parse(options.GuildId));
        return Task.FromResult(guild.Name);
    }
}