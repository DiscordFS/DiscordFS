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
            readResult.BytesRead = 0;


            //Almost every chunk starts on offset 0 except the first chunk of course
            //Also chunks out of bound need to be ignored
            //For last the ned to calculate the size of the chunk to save
            //And also the buffer offset

            //We start by creating a reading pointer and increment it with size
            //If reading pointer + size < offset that means chunk is out of bound
            //Also if pointer > offset + count that means chunk is out of bound

            //bufferOffset starts with offsetBuffer and incremeants by the size to match the buffer entry point
            //For last chunkOffset it is only use for the first chunk to match offset value if not match the begging of the first chunk position

            var readPosition = 0L;
            var bufferOffset = offsetBuffer;
            var preCalculateOffsetsSizes = new Dictionary<string, (int, int, int)>();//(chunkOffset, bufferOffset, size)

            foreach (var chunkInfo in _entry.Chunks)
            {
                // chunks outside the bound, ignoring
                var maxPosition = offset + count;
                if (readPosition >= maxPosition)
                {
                    continue;
                }

                var size = chunkInfo.Size;
                if (readPosition + size <= offset)
                {
                    readPosition += size;
                    continue;
                }

                // first chunk offset
                var chunkOffset = 0;
                if (readPosition < offset)
                {
                    size = (int)(readPosition + size - offset);
                    chunkOffset = (int)(offset - readPosition);
                }

                // last chunk offset
                if (readPosition + size > maxPosition)
                {
                    //same as (offset + count - currentPosition)
                    size = (int)(maxPosition - readPosition);
                }

                preCalculateOffsetsSizes.Add(chunkInfo.Url, (chunkOffset, bufferOffset, size));
                bufferOffset += size;
                readPosition += size;
            }

            await Parallel.ForEachAsync(_entry.Chunks, new ParallelOptions
            {
                CancellationToken = _openAsyncParams.CancellationToken,
                MaxDegreeOfParallelism = -1// todo: change this value and maybe set it in a global way
            }, async (chunkInfo, _) =>
            {
                if (!preCalculateOffsetsSizes.TryGetValue(chunkInfo.Url, out (int chunkOffset, int bufferOffset, int size) offsetsSizes))
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
                        offsetsSizes.chunkOffset,                        
                        buffer,
                        offsetsSizes.bufferOffset,
                        offsetsSizes.size);

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