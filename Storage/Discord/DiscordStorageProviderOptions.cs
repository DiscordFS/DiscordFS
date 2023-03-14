namespace DiscordFS.Storage.Discord;

public class DiscordStorageProviderOptions : StorageProviderOptions
{
    public required string BotToken { get; init; }

    public required string DataChannelName { get; set; }

    public required string DbChannelName { get; set; }

    public required string GuildId { get; init; }
}