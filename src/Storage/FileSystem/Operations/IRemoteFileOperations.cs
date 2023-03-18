using DiscordFS.Storage.FileSystem.Results;

namespace DiscordFS.Storage.FileSystem.Operations;

public interface IRemoteFileOperations : IDisposable
{
    public Task<CreateFileResult> CreateFileAsync(string relativeFileName, bool isDirectory);

    public Task<DeleteFileResult> DeleteFileAsync(string relativeFileName, bool isDirectory);

    public Task<GetFileInfoResult> GetFileInfoAsync(string relativeFileName, bool isDirectory);

    public IFileListStream GetNewFileList();

    public IReadFileStream GetNewReadFile();

    public IWriteFileStream GetNewWriteFile();

    public Task<MoveFileResult> MoveFileAsync(string relativeFileName, string relativeDestination, bool isDirectory);
}