using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.Synchronization;

namespace DiscordFS.Storage.FileSystem;

public class ProcessChangedDataArgs
{
    public string FullPath { get; set; }

    public ExtendedPlaceholderState LocalPlaceHolder { get; set; }

    public DynamicServerPlaceholder RemotePlaceholder { get; set; }

    public SyncMode SyncMode { get; set; }
}