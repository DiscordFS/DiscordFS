using DiscordFS.Storage.Files.Results;

namespace DiscordFS.Storage.Files;

public interface IRemoteFileOperations : IDisposable
{
    public Task<CreateFileResult> CreateFileAsync(string relativeFileName, bool isDirectory);

    public Task<DeleteFileResult> DeleteFileAsync(string relativeFileName, bool isDirectory);

    public Task<GetFileInfoResult> GetFileInfoAsync(string relativeFileName, bool isDirectory);

    public IFileListAsync GetNewFileList();

    public IReadFileAsync GetNewReadFile();

    public IWriteFileAsync GetNewWriteFile();

    public Task<MoveFileResult> MoveFileAsync(string relativeFileName, string relativeDestination, bool isDirectory);
}