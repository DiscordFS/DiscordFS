namespace DiscordFS.Configuration;

public class AppConfiguration
{
    public DiscordConfiguration Discord { get; set; }

    public string EncryptionKey { get; set; }

    public string LocalSyncPath { get; set; }

    public bool UseCompression { get; set; } = true;

    public AppConfiguration()
    {
        Discord = new DiscordConfiguration();
    }
}

public class DiscordConfiguration
{
    public string BotToken { get; set; }

    public string DataChannel { get; set; } = "#data";

    public string DbChannel { get; set; } = "#db";

    public bool Enabled { get; set; } = true;

    public string GuildId { get; set; }
}