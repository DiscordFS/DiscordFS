using DiscordFS.Storage.Files;

namespace DiscordFS.Storage.Synchronization;

public class SyncDataParam
{
    public string Folder { get; set; }

    public SyncMode SyncMode { get; set; }

    public CancellationToken CancellationToken { get; set; }
}