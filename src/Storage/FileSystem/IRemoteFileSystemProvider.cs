using DiscordFS.Storage.FileSystem.Operations;
using DiscordFS.Storage.FileSystem.Results;

namespace DiscordFS.Storage.FileSystem;

public interface IRemoteFileSystemProvider : IDisposable
{
    StorageProviderOptions Options { get; }

    public FileSystemProviderStatus Status { get; }

    event EventHandler<FileProviderStateChangedEventArgs> StateChange;

    IRemoteFileOperations Operations { get; }

    void Connect();

    Task<WriteFileCloseResult> UploadFileAsync(string fullPath, CancellationToken ctx);
}