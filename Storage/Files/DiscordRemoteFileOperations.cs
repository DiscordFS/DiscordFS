using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.Discord;
using DiscordFS.Storage.Files.Results;

namespace DiscordFS.Storage.Files;

public class DiscordRemoteFileOperations : IRemoteFileOperations
{
    private readonly DiscordRemoteFileSystemProvider _fileSystemProvider;
    private bool _disposed;

    public DiscordRemoteFileOperations(DiscordRemoteFileSystemProvider fileSystemProvider)
    {
        _fileSystemProvider = fileSystemProvider;
    }


    public async Task<CreateFileResult> CreateFileAsync(string relativeFileName, bool isDirectory)
    {
        EnsureNotDisposed();

        if (_fileSystemProvider.LastKnownRemoteIndex == null)
        {
            return new CreateFileResult(CloudFileFetchErrorCode.Offline);
        }

        var createFileResult = new CreateFileResult();
        var index = _fileSystemProvider.LastKnownRemoteIndex.Clone();

        try
        {
            IndexEntry result = null;

            if (isDirectory)
            {
                if (!index.DirectoryExists(relativeFileName))
                {
                    result = index.CreateDirectory(relativeFileName);
                    createFileResult.Status = CloudFilterNTStatus.STATUS_SUCCESS;
                    createFileResult.Succeeded = true;
                }
            }
            else
            {
                if (index.FileExists(relativeFileName))
                {
                    createFileResult.Status = CloudFilterNTStatus.STATUS_CLOUD_FILE_IN_USE;
                    createFileResult.Message = "File already exists";
                    createFileResult.Succeeded = false;
                }
                else
                {
                    result = index.CreateEmptyFile(relativeFileName);
                }
            }

            createFileResult.FilePlaceholder = new FilePlaceholder(result);
            await _fileSystemProvider.WriteIndexAsync(index);
        }
        catch (Exception ex)
        {
            createFileResult.SetException(ex);
        }

        return createFileResult;
    }

    public async Task<DeleteFileResult> DeleteFileAsync(string relativeFileName, bool isDirectory)
    {
        EnsureNotDisposed();

        if (_fileSystemProvider.LastKnownRemoteIndex == null)
        {
            return new DeleteFileResult(CloudFileFetchErrorCode.Offline);
        }

        var deleteFileResult = new DeleteFileResult();
        var index = _fileSystemProvider.LastKnownRemoteIndex.Clone();
        try
        {
            if (isDirectory)
            {
                index.RemoveDirectory(relativeFileName);
            }
            else
            {
                index.RemoveFile(relativeFileName);
            }

            await _fileSystemProvider.WriteIndexAsync(index);
        }
        catch (DirectoryNotFoundException ex)
        {
            // Directory already deleted?
            deleteFileResult.Message = ex.Message;
        }
        catch (FileNotFoundException ex)
        {
            // File already deleted?
            deleteFileResult.Message = ex.Message;
        }
        catch (Exception ex)
        {
            deleteFileResult.SetException(ex);
        }

        return deleteFileResult;
    }

    public Task<GetFileInfoResult> GetFileInfoAsync(string relativeFileName, bool isDirectory)
    {
        EnsureNotDisposed();

        if (_fileSystemProvider.LastKnownRemoteIndex == null)
        {
            return Task.FromResult(new GetFileInfoResult(CloudFileFetchErrorCode.Offline));
        }

        var getFileInfoResult = new GetFileInfoResult();
        var index = _fileSystemProvider.LastKnownRemoteIndex.Clone();

        try
        {
            IndexEntry entry;

            if (isDirectory)
            {
                if (!index.DirectoryExists(relativeFileName))
                {
                    return Task.FromResult(new GetFileInfoResult(CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE));
                }

                entry = index.GetDirectory(relativeFileName);
            }
            else
            {
                if (!index.FileExists(relativeFileName))
                {
                    return Task.FromResult(new GetFileInfoResult(CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE));
                }

                entry = index.GetFile(relativeFileName);
            }

            getFileInfoResult.Placeholder = new FilePlaceholder(entry);
        }
        catch (Exception ex)
        {
            getFileInfoResult.SetException(ex);
        }

        return Task.FromResult(getFileInfoResult);
    }

    public IFileListAsync GetNewFileList()
    {
        return new GetFileListAsync(_fileSystemProvider);
    }

    public IReadFileAsync GetNewReadFile()
    {
        return new ReadFileAsync(_fileSystemProvider);
    }

    public IWriteFileAsync GetNewWriteFile()
    {
        return new WriteFileAsync(_fileSystemProvider);
    }

    public async Task<MoveFileResult> MoveFileAsync(string relativeFileName, string relativeDestination, bool isDirectory)
    {
        EnsureNotDisposed();

        if (_fileSystemProvider.LastKnownRemoteIndex == null)
        {
            return new MoveFileResult(CloudFileFetchErrorCode.Offline);
        }

        var index = _fileSystemProvider.LastKnownRemoteIndex.Clone();

        if (!index.DirectoryExists(Path.GetDirectoryName(relativeDestination)))
        {
            index.CreateDirectory(Path.GetDirectoryName(relativeDestination));
        }

        var moveFileResult = new MoveFileResult();

        try
        {
            if (isDirectory)
            {
                index.MoveDirectory(relativeFileName, relativeDestination);
            }
            else
            {
                index.MoveFile(relativeFileName, relativeDestination);
            }

            await _fileSystemProvider.WriteIndexAsync(index);
        }
        catch (Exception ex)
        {
            moveFileResult.SetException(ex);
        }

        return moveFileResult;
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }
}