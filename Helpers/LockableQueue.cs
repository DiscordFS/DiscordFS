using System.Collections.Concurrent;
using DiscordFS.Storage;

namespace DiscordFS.Helpers;

public class LockableQueue<TItem> : IDisposable
{
    public CancellationToken CancellationToken
    {
        get { return _cancellationTokenSource.Token; }
    }

    private readonly Timer _addTimer;
    private readonly AutoResetEventAsync _autoResetEventAsync = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly List<TItem> _itemsToProcess = new();
    private readonly object _lockObject = new();
    private readonly ConcurrentDictionary<TItem, int> _lockTable = new();

    private bool _disposedValue;

    public int RestartDelay = 1000;
    public int WaitForAddingCompleted = 4000;

    public LockableQueue()
    {
        _addTimer = new Timer(AddTimerCallback, state: null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public void Add(TItem data, bool ignoreLock)
    {
        TryAdd(data, ignoreLock);
    }

    public void Cancel(TItem data)
    {
        lock (_lockObject)
        {
            _itemsToProcess.Remove(data);
        }
    }

    public void Complete()
    {
        _cancellationTokenSource.Cancel();
    }

    public int Count()
    {
        lock (_lockObject)
        {
            return _itemsToProcess.Count;
        }
    }

    public bool IsItemLocked(TItem item)
    {
        _lockTable.TryGetValue(item, out var value);

        return value != 0;
    }

    public void LockItem(TItem item)
    {
        _lockTable.AddOrUpdate(item, addValue: 1, (_, v) => v + 1);
    }

    /// <summary>
    ///     Locks item for subsequent adds... While the LockItem is not Disposed, the list does not add the item to list if it
    ///     is requested by Add or TryAdd
    /// </summary>
    /// <param name="item"></param>
    /// <returns>IDisposable Item which holds the Lock for the item and releases the lock after disposable</returns>
    public DisposableObject<TItem> LockItemDisposable(TItem item)
    {
        _lockTable.AddOrUpdate(item, addValue: 1, (_, v) => v + 1);

        return new DisposableObject<TItem>(_ =>
        {
            if (_lockTable.AddOrUpdate(item, addValue: 0, (_, v) => v - 1) <= 0)
            {
                _addTimer.Change(WaitForAddingCompleted, Timeout.Infinite);
            }
        }, item);
    }

    public void Reset()
    {
        _itemsToProcess.Clear();
        _lockTable.Clear();
    }

    public bool TryAdd(TItem data, bool ignoreLock)
    {
        lock (_lockObject)
        {
            if (!_itemsToProcess.Contains(data))
            {
                if (!ignoreLock && IsItemLocked(data))
                {
                    return false;
                }

                _itemsToProcess.Add(data);

                _addTimer.Change(WaitForAddingCompleted, Timeout.Infinite);

                return true;
            }
        }

        return false;
    }

    public void UnlockItem(TItem item)
    {
        if (_lockTable.AddOrUpdate(item, addValue: 0, (_, v) => v - 1) <= 0)
        {
            _addTimer.Change(WaitForAddingCompleted, Timeout.Infinite);
        }
    }

    public Task<TItem> WaitTakeNextAsync()
    {
        lock (_lockObject)
        {
            if (TryTakeNext(out var data))
            {
                return Task.FromResult(data);
            }

            return Task.Run(async () =>
            {
                await _autoResetEventAsync.WaitAsync(CancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                if (RestartDelay > 0)
                {
                    await Task.Delay(RestartDelay, CancellationToken);
                }

                return await WaitTakeNextAsync();
            }, CancellationToken);
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _cancellationTokenSource.Cancel();
            }

            _disposedValue = true;
        }
    }

    internal bool TryTakeNext(out TItem data)
    {
        if (_itemsToProcess.Any())
        {
            foreach (var item in _itemsToProcess)
            {
                if (IsItemLocked(item))
                {
                    continue;
                }

                data = item;
                _itemsToProcess.Remove(item);
                return true;
            }

            data = default;
            return false;
        }

        data = default;
        return false;
    }

    private void AddTimerCallback(object stateInfo)
    {
        _autoResetEventAsync.Set();
    }
}