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

    public async Task<ReadFileReadResult> ReadAsync(byte[] buffer, int offsetBuffer, long chunksOffset, int count)
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
            readResult.BytesRead = 0;

            //This peace of code will calculate the offset for all chunks used in blockcopy
            //The calculation is based on input and chunk size
            var currentChunkPos = 0L;
            var bufferOffset = offsetBuffer;
            var preCalculateOffsetSize = new Dictionary<string, (int, int)>();//(offset, size)

            foreach (var chunkInfo in _entry.Chunks)
            {
                var size = chunkInfo.Size;

                // chunks outside the bound, ignoring
                if (currentChunkPos + size <= chunksOffset)
                {
                    currentChunkPos += size;
                    continue;
                }

                // first chunk
                if (currentChunkPos < chunksOffset)
                {
                    size = (int)(currentChunkPos + size - currentChunkPos);
                    currentChunkPos = chunksOffset;
                }

                // last chunk
                var maxPosition = chunksOffset + count;
                if (currentChunkPos + size > maxPosition)
                {
                    //same as (offset + count - currentPosition)
                    size = (int)(maxPosition - currentChunkPos);
                }

                preCalculateOffsetSize.Add(chunkInfo.Url, (bufferOffset, size));
                bufferOffset += size;
                currentChunkPos += size;
            }


            await Parallel.ForEachAsync(_entry.Chunks, new ParallelOptions
            {
                CancellationToken = _openAsyncParams.CancellationToken,
                MaxDegreeOfParallelism = -1// todo: change this value and maybe set it in a global way
            }, async (chunkInfo, _) =>
            {
                if (!preCalculateOffsetSize.TryGetValue(chunkInfo.Url, out (int offset, int size) chunkOffsetSize))
                {
                    //if not found it must be ignored
                    return;
                }

                var chunk = await DownloadChunkAsync(chunkInfo);
                var size = chunk.Data.Length;

                lock (buffer)
                {
                    Buffer.BlockCopy(
                        chunk.Data,
                        srcOffset: 0,
                        buffer,
                        chunkOffsetSize.offset,
                        chunkOffsetSize.size);

                    readResult.BytesRead += size;
                }
            });

            if (readResult.BytesRead != count)
            {
                throw new Exception(message: "Failed to read all requested bytes");
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
        return FileChunk.Deserialize(data, _discordFs.Options.EncryptionKey);
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