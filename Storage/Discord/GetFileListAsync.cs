using System.Collections.Concurrent;
using System.Diagnostics;
using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.Files;
using DiscordFS.Storage.Files.Results;
using DiscordFS.Storage.Helpers;

namespace DiscordFS.Storage.Discord;

public class GetFileListAsync : IFileListAsync
{
    private readonly IRemoteFileProvider _provider;
    private readonly CancellationTokenSource _ctx;
    private readonly BlockingCollection<FilePlaceholder> _infoList;
    private readonly FileOperationResult _finalStatus;

    public bool IsOpen { get; protected set; }

    public GetFileListAsync(IRemoteFileProvider provider)
    {
        _provider = provider;
        _ctx = new CancellationTokenSource();
        _infoList = new BlockingCollection<FilePlaceholder>();
        _finalStatus = new FileOperationResult();
    }

    public Task<FileOperationResult> CloseAsync()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException(message: "Not open");
        }

        _ctx.Cancel();

        if (!_infoList.IsAddingCompleted)
        {
            _infoList.CompleteAdding();
            _finalStatus.Status = CloudFilterNTStatus.STATUS_CLOUD_FILE_REQUEST_ABORTED;
        }

        IsOpen = false;
        return Task.FromResult(_finalStatus);
    }

    public Task<GetNextFileResult> GetNextAsync()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException(message: "Not open");
        }

        return Task.Run(GetNextFileResult() =>
        {
            var getNextResult = new GetNextFileResult();

            try
            {
                if (_provider.Status != FileProviderStatus.Ready)
                {
                    return new GetNextFileResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE);
                }

                if (_infoList.TryTake(out var item, millisecondsTimeout: -1, _ctx.Token))
                {
                    // STATUS_SUCCESS = Data found
                    getNextResult.Status = CloudFilterNTStatus.STATUS_SUCCESS;
                    getNextResult.FilePlaceholder = item;
                }
                else
                {
                    // STATUS_UNSUCCESSFUL = No more Data available.
                    getNextResult.Status = CloudFilterNTStatus.STATUS_UNSUCCESSFUL;
                }
            }
            catch (Exception ex)
            {
                getNextResult.SetException(ex);
                _finalStatus.SetException(ex);
            }

            return getNextResult;
        });
    }

    public Task<FileOperationResult> OpenAsync(string relativeFileName, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (IsOpen)
        {
            throw new InvalidOperationException(message: "Already open");
        }

        if (_provider.Status != FileProviderStatus.Ready)
        {
            return Task.FromResult(new FileOperationResult(CloudFileFetchErrorCode.Offline));
        }

        IsOpen = true;

        var fullPath = PathHelper.GetAbsolutePath(relativeFileName, _provider.Options.LocalPath);
        cancellationToken.Register(_ctx.Cancel);
        var tctx = _ctx.Token;

        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists)
        {
            return Task.FromResult(new FileOperationResult(CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE));
        }

        Task.Run(() =>
        {
            try
            {
                foreach (var fileSystemInfo in directory.EnumerateFileSystemInfos())
                {
                    tctx.ThrowIfCancellationRequested();

                    if (!fileSystemInfo.Name.StartsWith(value: @"$"))
                    {
                        _infoList.Add(new FilePlaceholder(fileSystemInfo), tctx);
                    }
                }
            }
            catch (Exception ex)
            {
                _finalStatus.SetException(ex);
            }
            finally
            {
                _infoList.CompleteAdding();
            }
        }, tctx);

        return Task.FromResult(new FileOperationResult());
    }

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _ctx?.Cancel();
            _infoList?.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (IsOpen)
        {
            await CloseAsync();
        }

        _infoList?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore();

        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }
}

public class WriteFileAsync : IWriteFileAsync
{
    private OpenAsyncParams _params;
    private readonly IRemoteFileProvider _provider;
    private FileStream _fileStream;
    private string _fullPath;
    private string _tempFile;

    public bool IsOpen { get; protected set; }

    public WriteFileAsync(IRemoteFileProvider provider)
    {
        _provider = provider;
    }

    public UploadMode SupportedUploadModes
    {
        get
        {
            // Resume currently not implemented (Verification of file integrity not implemented)
            return UploadMode.FullFile | UploadMode.PartialUpdate;
        }
    }

    public Task<WriteFileOpenResult> OpenAsync(OpenAsyncParams e)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (IsOpen)
        {
            throw new InvalidOperationException(message: "Already open");
        }

        if (_provider.Status != FileProviderStatus.Ready)
        {
            return Task.FromResult(new WriteFileOpenResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE));
        }

        _params = e;

        var openResult = new WriteFileOpenResult();

        // PartialUpdate is done In-Place without temp file.
        if (e.UploadMode == UploadMode.PartialUpdate)
        {
            _useTempFilesForUpload = false;
        }

        try
        {
            _fullPath = PathHelper.GetAbsolutePath(_params.RelativeFileName, _provider.Options.LocalPath);

            if (!Directory.Exists(Path.GetDirectoryName(_fullPath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_fullPath)!);
            }

            _tempFile = Path.GetDirectoryName(_fullPath) + @"\$_" + Path.GetFileName(_fullPath);

            var fileMode = _params.UploadMode switch
            {
                UploadMode.FullFile => FileMode.Create,
                UploadMode.Resume => FileMode.Open,
                UploadMode.PartialUpdate => FileMode.Open,
                _ => FileMode.OpenOrCreate
            };

            _fileStream = _useTempFilesForUpload
                ? new FileStream(_tempFile, fileMode, FileAccess.Write, FileShare.None)
                : new FileStream(_fullPath, fileMode, FileAccess.Write, FileShare.None);


            _fileStream.SetLength(e.FileInfo.FileSize);
            if (File.Exists(_fullPath))
            {
                openResult.Placeholder = new FilePlaceholder(_fullPath);
            }
        }
        catch (Exception ex)
        {
            openResult.SetException(ex);
        }

        return Task.FromResult(openResult);
    }

    private bool _useTempFilesForUpload;

    public async Task<WriteFileWriteResult> WriteAsync(byte[] buffer, int offsetBuffer, long offset, int count)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException(message: "Not open");
        }

        var writeResult = new WriteFileWriteResult();

        try
        {
            _fileStream.Position = offset;
            await _fileStream.WriteAsync(buffer, offsetBuffer, count, _params.CancellationToken);
        }
        catch (Exception ex)
        {
            writeResult.SetException(ex);
        }

        return writeResult;
    }

    public async Task<WriteFileCloseResult> CloseAsync(bool isCompleted)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException(message: "Not open");
        }

        var closeResult = new WriteFileCloseResult();

        try
        {
            await _fileStream.FlushAsync();
            _fileStream.Close();
            IsOpen = false;
            await _fileStream.DisposeAsync();

            var filePath = _fullPath;

            if (_useTempFilesForUpload)
            {
                filePath = _tempFile;
            }

            try
            {
                var att = _params.FileInfo.FileAttributes;
                att &= ~FileAttributes.ReadOnly;

                if (_params.FileInfo.FileAttributes > 0)
                {
                    File.SetAttributes(filePath, att);
                }

                if (_params.FileInfo.CreationTime > DateTime.MinValue)
                {
                    File.SetCreationTime(filePath, _params.FileInfo.CreationTime);
                }

                if (_params.FileInfo.LastAccessTime > DateTime.MinValue)
                {
                    File.SetLastAccessTime(filePath, _params.FileInfo.LastAccessTime);
                }

                if (_params.FileInfo.LastWriteTime > DateTime.MinValue)
                {
                    File.SetLastWriteTime(filePath, _params.FileInfo.LastWriteTime);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            if (isCompleted)
            {
                if (_useTempFilesForUpload)
                {
                    if (File.Exists(_fullPath))
                    {
                        File.Delete(_fullPath);
                    }

                    File.Move(_tempFile, _fullPath);
                }

                closeResult.Placeholder = new FilePlaceholder(_fullPath);
            }
        }
        catch (Exception ex)
        {
            closeResult.SetException(ex);
        }

        return closeResult;
    }

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                try
                {
                    if (IsOpen)
                    {
                        IsOpen = false;

                        _fileStream?.Flush();
                        _fileStream?.Close();
                    }
                }
                finally
                {
                    _fileStream?.Dispose();
                }
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        try
        {
            if (IsOpen)
            {
                IsOpen = false;
                if (_fileStream != null)
                {
                    await _fileStream.FlushAsync();
                    _fileStream.Close();
                }
            }
        }
        finally
        {
            if (_fileStream != null)
            {
                await _fileStream.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore();

        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }
}