namespace DiscordFS.Storage.Synchronization;

public class SyncDataParam
{
    public CancellationToken CancellationToken { get; set; }

    public string Folder { get; set; }

    public SyncMode SyncMode { get; set; }
}