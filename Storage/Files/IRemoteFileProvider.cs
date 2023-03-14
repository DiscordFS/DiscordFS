using DiscordFS.Storage.Files.Results;

namespace DiscordFS.Storage.Files;

public interface IRemoteFileProvider : IDisposable
{
    StorageProviderOptions Options { get; }

    public FileProviderStatus Status { get; }

    event EventHandler<FileProviderStateChangedEventArgs> StateChange;

    void Connect();

    public Task<CreateFileResult> CreateFileAsync(string relativeFileName, bool isDirectory);

    public Task<DeleteFileResult> DeleteFileAsync(string relativeFileName, bool isDirectory);

    public Task<GetFileInfoResult> GetFileInfo(string relativeFileName, bool isDirectory);

    public IFileListAsync GetNewFileList();

    public IReadFileAsync GetNewReadFile();

    public IWriteFileAsync GetNewWriteFile();

    public Task<MoveFileResult> MoveFileAsync(string relativeFileName, string relativeDestination, bool isDirectory);

    Task<WriteFileCloseResult> UploadFileToServerAsync(string fullPath, CancellationToken ctx);
}