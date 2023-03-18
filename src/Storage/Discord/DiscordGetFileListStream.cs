using System.Collections.Concurrent;
using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.FileSystem;
using DiscordFS.Storage.FileSystem.Operations;
using DiscordFS.Storage.FileSystem.Results;
using Index = DiscordFS.Storage.Synchronization.Index;

namespace DiscordFS.Storage.Discord;

public class DiscordGetFileListStream : IFileListStream
{
    private readonly DiscordRemoteFileSystemProvider _discordFs;
    private readonly CancellationTokenSource _ctx;
    private readonly BlockingCollection<FilePlaceholder> _infoList;
    private readonly FileOperationResult _finalStatus;

    public bool IsOpen { get; protected set; }

    public DiscordGetFileListStream(DiscordRemoteFileSystemProvider discordFs)
    {
        _discordFs = discordFs;
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
                if (_discordFs.Status != FileSystemProviderStatus.Ready)
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

    public Task<FileOperationResult> OpenAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (IsOpen)
        {
            throw new InvalidOperationException(message: "Already open");
        }

        _index = _discordFs.LastKnownRemoteIndex?.Clone();
        if (_discordFs.Status != FileSystemProviderStatus.Ready || _index == null)
        {
            return Task.FromResult(new FileOperationResult(CloudFileFetchErrorCode.Offline));
        }

        IsOpen = true;

        cancellationToken.Register(_ctx.Cancel);
        var tctx = _ctx.Token;

        try
        {
            foreach (var entry in _index.EnumerateDirectory(relativePath))
            {
                tctx.ThrowIfCancellationRequested();

                if (!FileExcluder.IsExcludedFile(entry.RelativePath))
                {
                    _infoList.Add(new FilePlaceholder(entry), tctx);
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

        return Task.FromResult(new FileOperationResult());
    }

    private bool _disposed;
    private Index _index;

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