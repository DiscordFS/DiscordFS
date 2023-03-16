using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.Files;
using DiscordFS.Storage.Files.Results;
using DiscordFS.Storage.Helpers;

namespace DiscordFS.Storage.Discord;

public class ReadFileAsync : IReadFileAsync
{
    private readonly IRemoteFileSystemProvider _systemProvider;
    private FileStream _fileStream;
    private OpenAsyncParams _openAsyncParams;

    public bool IsOpen { get; protected set; }

    public ReadFileAsync(IRemoteFileSystemProvider systemProvider)
    {
        _systemProvider = systemProvider;
    }

    public Task<ReadFileOpenResult> OpenAsync(OpenAsyncParams e)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (IsOpen)
        {
            throw new InvalidOperationException(message: "Already open");
        }

        if (_systemProvider.Status != FileProviderStatus.Ready)
        {
            return Task.FromResult(new ReadFileOpenResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE));
        }

        IsOpen = true;

        _openAsyncParams = e;
        var openResult = new ReadFileOpenResult();

        if (!Directory.Exists(_systemProvider.Options.LocalPath))
        {
            openResult.SetError(CloudFileFetchErrorCode.Offline);
            goto skip;
        }

        var fullPath = PathHelper.GetAbsolutePath(e.RelativeFileName, _systemProvider.Options.LocalPath);

        try
        {
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(e.RelativeFileName);
            }

            _fileStream = File.OpenRead(fullPath);
            openResult.Placeholder = new FilePlaceholder(fullPath);
        }
        catch (Exception ex)
        {
            openResult.SetException(ex);
        }

        skip:
        return Task.FromResult(openResult);
    }

    public async Task<ReadFileReadResult> ReadAsync(byte[] buffer, int offsetBuffer, long offset, int count)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException(message: "Not open");
        }

        if (_systemProvider.Status != FileProviderStatus.Ready)
        {
            return new ReadFileReadResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE);
        }

        var readResult = new ReadFileReadResult();

        try
        {
            _fileStream.Position = offset;
            readResult.BytesRead = await _fileStream.ReadAsync(buffer, offsetBuffer, count, _openAsyncParams.CancellationToken);
        }
        catch (Exception ex)
        {
            readResult.SetException(ex);
        }

        return readResult;
    }

    public Task<ReadFileCloseResult> CloseAsync()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException(message: "Not open");
        }

        var closeResult = new ReadFileCloseResult();

        try
        {
            _fileStream.Close();
            IsOpen = false;
        }
        catch (Exception ex)
        {
            closeResult.SetException(ex);
        }

        return Task.FromResult(closeResult);
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
                }

                _fileStream?.Close();
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