using DiscordFS.Helpers;
using DiscordFS.Storage.Actions;
using Vanara.PInvoke;

namespace DiscordFS.Storage.FileSystem;

public class FetchRange
{
    public string NormalizedPath { get; set; }

    public byte PriorityHint { get; set; }

    public long RangeEnd { get; set; }

    public long RangeStart { get; set; }

    public CldApi.CF_TRANSFER_KEY TransferKey { get; set; }

    public FetchRange() { }

    public FetchRange(DataActions data)
    {
        NormalizedPath = data.NormalizedPath;
        PriorityHint = data.PriorityHint;
        RangeStart = data.FileOffset;
        RangeEnd = data.FileOffset + data.Length;
        TransferKey = data.TransferKey;
    }
}

public class FileRangeManager : IDisposable
{
    public CancellationToken CancellationToken
    {
        get { return _cancellationToken.Token; }
    }

    private readonly AutoResetEventAsync _autoResetEventAsync = new();
    private readonly CancellationTokenSource _cancellationToken = new();
    private readonly List<FetchRange> _filesToProcess = new();
    private readonly object _lockObject = new();

    private bool _disposedValue;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public void Add(DataActions data)
    {
        lock (_lockObject)
        {
            _filesToProcess.Add(new FetchRange(data));
            Combine(data.NormalizedPath);
        }

        _autoResetEventAsync.Set();
    }

    public void Cancel(DataActions data)
    {
        var removeItems = new List<FetchRange>();
        var addItems = new List<FetchRange>();

        lock (_lockObject)
        {
            var rangeStart = data.FileOffset;
            var rangeEnd = data.FileOffset + data.Length;

            foreach (var item in _filesToProcess
                         .Where(a => a.NormalizedPath == data.NormalizedPath && a.RangeStart <= rangeEnd && a.RangeEnd >= rangeStart)
                         .OrderBy(a => a.RangeStart))
            {
                if (item.RangeStart >= rangeStart && item.RangeEnd <= rangeEnd)
                {
                    item.RangeStart = 0;
                    item.RangeEnd = 0;
                    removeItems.Add(item);
                    continue;
                }

                if (item.RangeStart >= rangeStart && item.RangeStart < rangeEnd)
                {
                    item.RangeStart = rangeEnd;
                }

                if (item.RangeEnd < rangeEnd && item.RangeEnd >= rangeStart)
                {
                    item.RangeEnd = rangeStart;
                }

                if (item.RangeStart < rangeStart && item.RangeEnd > rangeEnd)
                {
                    var newItem = new FetchRange
                    {
                        NormalizedPath = item.NormalizedPath,
                        PriorityHint = item.PriorityHint,
                        TransferKey = item.TransferKey,
                        RangeStart = rangeEnd + 1,
                        RangeEnd = item.RangeEnd
                    };

                    item.RangeEnd = rangeStart;

                    addItems.Add(newItem);
                }

                if (item.RangeEnd <= item.RangeStart)
                {
                    removeItems.Add(item);
                }

                if (item.RangeStart < 0)
                {
                    throw new ArgumentException(message: "RangeStart < 0");
                }
            }

            foreach (var item in removeItems)
            {
                _filesToProcess.Remove(item);
            }

            foreach (var item in addItems)
            {
                _filesToProcess.Add(item);
            }

            Combine(data.NormalizedPath);
        }
    }

    public void Cancel(string normalizedPath)
    {
        RemoveRange(normalizedPath, rangeStart: 0, long.MaxValue);
    }

    public void RemoveRange(string normalizedPath, long rangeStart, long rangeEnd)
    {
        Cancel(new DataActions
        {
            NormalizedPath = normalizedPath,
            FileOffset = rangeStart,
            Length = rangeEnd - rangeStart
        });
    }

    public FetchRange TakeNext()
    {
        FetchRange ret;

        lock (_lockObject)
        {
            ret = _filesToProcess.OrderByDescending(a => a.PriorityHint)
                .ThenBy(a => a.RangeStart)
                .FirstOrDefault();
        }

        return ret;
    }

    public FetchRange TakeNext(string normalizedPath)
    {
        FetchRange ret = null;

        lock (_lockObject)
        {
            ret = _filesToProcess
                .Where(a => a.NormalizedPath == normalizedPath)
                .MinBy(a => a.RangeStart);
        }

        return ret;
    }

    public async Task<FetchRange> WaitTakeNextAsync()
    {
        var x = TakeNext();

        if (x == null)
        {
            await _autoResetEventAsync.WaitAsync(CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            lock (_lockObject)
            {
                var t = TakeNext();
                return t;
            }
        }

        return x;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _cancellationToken.Cancel();
            }

            _disposedValue = true;
        }
    }

    private void Combine(string normalizedPath)
    {
        var exitLoop = false;
        while (!exitLoop)
        {
            exitLoop = true;

            foreach (var x in _filesToProcess
                         .Where(a => a.NormalizedPath == normalizedPath)
                         .OrderBy(a => a.RangeStart))
            {
                var y = _filesToProcess
                    .Where(a => a.NormalizedPath == normalizedPath
                                && a.RangeStart <= x.RangeEnd
                                && a.RangeEnd >= x.RangeStart
                                && a != x)
                    .MinBy(a => a.RangeStart);

                if (y != null)
                {
                    x.RangeStart = Math.Min(x.RangeStart, y.RangeStart);
                    x.RangeEnd = Math.Min(x.RangeEnd, y.RangeEnd);
                    _filesToProcess.Remove(y);

                    exitLoop = false;
                    break;
                }
            }
        }
    }
}