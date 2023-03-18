using Discord;
using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.FileSystem;
using DiscordFS.Storage.FileSystem.Operations;
using DiscordFS.Storage.FileSystem.Results;
using DiscordFS.Storage.Synchronization;

namespace DiscordFS.Storage.Discord;

public class DiscordWriteFileStream : IWriteFileStream
{
    public const int MaxAttachmentSize = 1024 * 1024 * 8;

    private OpenAsyncParams _params;
    private readonly DiscordRemoteFileSystemProvider _discordFs;

    private List<IndexFileChunk> _chunks;
    private int _chunkIndex;

    public bool IsOpen { get; protected set; }

    public DiscordWriteFileStream(DiscordRemoteFileSystemProvider discordFs)
    {
        _discordFs = discordFs;
    }

    public UploadMode SupportedUploadModes
    {
        get
        {
            // Resume currently not implemented (Verification of file integrity not implemented)
            return UploadMode.FullFile;
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

        if (_discordFs.Status != FileSystemProviderStatus.Ready)
        {
            return Task.FromResult(new WriteFileOpenResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE));
        }

        IsOpen = true;

        _params = e;
        _chunks = new List<IndexFileChunk>();

        var openResult = new WriteFileOpenResult();
        return Task.FromResult(openResult);
    }

    private async Task UploadChunkAsync(FileChunk chunk)
    {
        _params.CancellationToken.ThrowIfCancellationRequested();

        var chunkDataSize = chunk.Data.Length;

        using var ms = new MemoryStream(chunk.Serialize());
        ms.Seek(offset: 0, SeekOrigin.Begin);

        var fileName = Guid.NewGuid().ToString(format: "N");
        var attachment = new FileAttachment(ms, fileName);

        var message = await _discordFs.DataChannel.SendFileAsync(attachment,
            options: DiscordHelper.CreateDefaultOptions(_params.CancellationToken));

        _chunks.AddRange(message.Attachments.Select(x => new IndexFileChunk
        {
            Size = chunkDataSize,
            Url = x.Url
        }));
    }

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

        if (_discordFs.Status != FileSystemProviderStatus.Ready)
        {
            writeResult.SetError(CloudFileFetchErrorCode.Offline);
            return writeResult;
        }

        try
        {
            foreach (var data in buffer.Chunk(MaxAttachmentSize))
            {
                var chunk = FileChunk.CreateChunk(
                    _chunkIndex,
                    data,
                    offset: 0,
                    data.Length,
                    _discordFs.Options.UseCompression);

                await UploadChunkAsync(chunk);
                _chunkIndex++;
            }
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

        IsOpen = false;

        var closeResult = new WriteFileCloseResult();
        var index = _discordFs.LastKnownRemoteIndex?.Clone();

        if (_discordFs.Status != FileSystemProviderStatus.Ready || index == null)
        {
            closeResult.SetError(CloudFileFetchErrorCode.Offline);
            return closeResult;
        }

        if (isCompleted)
        {
            try
            {
                var relativePath = _params.RelativePath;
                var fileInfo = _params.FileInfo;

                var offset = TimeZoneInfo.Local.BaseUtcOffset;

                var entry = index.CreateEmptyFile(relativePath, overwrite: true);
                entry.Attributes = fileInfo.FileAttributes;
                entry.CreationTime = fileInfo.CreationTime != default
                    ? new DateTimeOffset(fileInfo.CreationTime, offset)
                    : DateTimeOffset.UtcNow;
                entry.LastAccessTime = fileInfo.LastAccessTime != default
                    ? new DateTimeOffset(fileInfo.LastAccessTime, offset)
                    : DateTimeOffset.UtcNow;
                entry.LastModificationTime = fileInfo.LastWriteTime != default
                    ? new DateTimeOffset(fileInfo.LastWriteTime, offset)
                    : DateTimeOffset.UtcNow;
                entry.FileSize = fileInfo.FileSize;
                entry.Chunks = _chunks;

                await _discordFs.WriteIndexAsync(index);

                var placeholder = new FilePlaceholder(entry);
                closeResult.Placeholder = placeholder;
            }
            catch (Exception ex)
            {
                closeResult.SetException(ex);
            }
        }

        return closeResult;
    }

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing && IsOpen)
            {
                IsOpen = false;
            }
        }

        _disposed = true;
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