namespace DiscordFS.Storage.Synchronization;

public class FailedDataSyncInfo
{
    public Exception LastException { get; set; }

    public DateTime LastTry { get; set; }

    public DateTime NextTry { get; set; }

    public int RetryCount { get; set; }

    public SyncMode SyncMode { get; set; }
}