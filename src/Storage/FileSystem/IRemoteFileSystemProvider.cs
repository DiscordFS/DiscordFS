using DiscordFS.Helpers;
using DiscordFS.Storage.FileSystem.Operations;

namespace DiscordFS.Storage.FileSystem;

public interface IRemoteFileSystemProvider : IDisposable
{
    event AsyncEventHandler<FileProviderStateChangedEventArgs> StateChange;

    event AsyncEventHandler<FileChangedEventArgs> FileChange;

    FileSystemProviderStatus Status { get; }

    IRemoteFileOperations Operations { get; }

    int ChunkSize { get; }

    void Connect();
}