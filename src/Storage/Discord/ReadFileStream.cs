using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.FileSystem;
using DiscordFS.Storage.FileSystem.Operations;
using DiscordFS.Storage.FileSystem.Results;
using DiscordFS.Storage.Synchronization;

namespace DiscordFS.Storage.Discord;

public class ReadFileStream : IReadFileStream
{
    private readonly DiscordRemoteFileSystemProvider _discordFs;

    private IndexEntry _entry;
    private OpenAsyncParams _openAsyncParams;
    private bool _disposed;

    public bool IsOpen { get; protected set; }

    public ReadFileStream(DiscordRemoteFileSystemProvider discordFs)
    {
        _discordFs = discordFs;
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

        var index = _discordFs.LastKnownRemoteIndex?.Clone();
        if (_discordFs.Status != FileSystemProviderStatus.Ready || index == null)
        {
            return Task.FromResult(new ReadFileOpenResult(CloudFileFetchErrorCode.Offline));
        }

        IsOpen = true;

        _openAsyncParams = e;
        var openResult = new ReadFileOpenResult();

        try
        {
            if (!index.FileExists(e.RelativePath))
            {
                throw new FileNotFoundException(e.RelativePath);
            }

            _entry = index.GetFile(e.RelativePath);
            openResult.Placeholder = new FilePlaceholder(_entry);
        }
        catch (Exception ex)
        {
            openResult.SetException(ex);
        }

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

        if (_discordFs.Status != FileSystemProviderStatus.Ready)
        {
            return new ReadFileReadResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE);
        }

        var readResult = new ReadFileReadResult();

        try
        {
            var currentPos = 0L;
            readResult.BytesRead = 0;

            // todo: Parallel.ForEachAsync(_entry.Chunks)

            foreach (var chunkInfo in _entry.Chunks)
            {
                if (currentPos >= offset && currentPos <= offset + count)
                {
                    var chunk = await DownloadChunkAsync(chunkInfo);
                    var size = chunk.Data.Length;

                    Buffer.BlockCopy(
                        chunk.Data,
                        srcOffset: 0,
                        buffer,
                        readResult.BytesRead + offsetBuffer,
                        size);

                    readResult.BytesRead += size;
                }

                currentPos += chunkInfo.Size;
            }
        }
        catch (Exception ex)
        {
            readResult.SetException(ex);
        }

        return readResult;
    }

    private async Task<FileChunk> DownloadChunkAsync(IndexFileChunk chunk)
    {
        // todo: cache attachments until X total size so we don't have to download them again

        var client = _discordFs.HttpClientFactory.CreateClient(name: "DiscordFS");
        var data = await client.GetByteArrayAsync(chunk.Url, _openAsyncParams.CancellationToken);
        return FileChunk.Deserialize(data);
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
            IsOpen = false;
        }
        catch (Exception ex)
        {
            closeResult.SetException(ex);
        }

        return Task.FromResult(closeResult);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                if (IsOpen)
                {
                    IsOpen = false;
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

    protected virtual ValueTask DisposeAsyncCore()
    {
        if (IsOpen)
        {
            IsOpen = false;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore();

        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }
}