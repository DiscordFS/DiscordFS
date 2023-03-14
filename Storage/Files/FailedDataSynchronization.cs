namespace DiscordFS.Storage.Files;

public class FailedDataSynchronization
{
    public DateTime LastTry { get; set; }

    public DateTime NextTry { get; set; }

    public Exception LastException { get; set; }

    public int RetryCount { get; set; }

    public SyncMode SyncMode { get; set; }
}