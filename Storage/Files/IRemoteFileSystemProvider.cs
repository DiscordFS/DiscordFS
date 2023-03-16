using DiscordFS.Storage.Files.Results;

namespace DiscordFS.Storage.Files;

public interface IRemoteFileSystemProvider : IDisposable
{
    StorageProviderOptions Options { get; }

    public FileProviderStatus Status { get; }

    event EventHandler<FileProviderStateChangedEventArgs> StateChange;

    IRemoteFileOperations Operations { get; }

    void Connect();

    Task<WriteFileCloseResult> UploadFileAsync(string fullPath, CancellationToken ctx);
}