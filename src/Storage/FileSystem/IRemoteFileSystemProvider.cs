using DiscordFS.Storage.FileSystem.Operations;

namespace DiscordFS.Storage.FileSystem;

public interface IRemoteFileSystemProvider : IDisposable
{
    StorageProviderOptions Options { get; }

    public FileSystemProviderStatus Status { get; }

    event EventHandler<FileProviderStateChangedEventArgs> StateChange;

    IRemoteFileOperations Operations { get; }

    void Connect();
}