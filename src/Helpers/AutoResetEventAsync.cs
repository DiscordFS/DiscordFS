#nullable enable

namespace DiscordFS.Helpers;

public sealed class AutoResetEventAsync : IDisposable
{
    private readonly Queue<SemaphoreSlim> _queue = new();
    private volatile bool _isSignaled;

    public void Dispose()
    {
        lock (_queue)
        {
            while (_queue.Count > 0)
            {
                _queue.Dequeue().Dispose();
            }
        }
    }

    public void Reset()
    {
        _isSignaled = false;
    }

    public void Set()
    {
        SemaphoreSlim? toRelease = null;
        lock (_queue)
        {
            if (_queue.Count > 0)
            {
                toRelease = _queue.Dequeue();
            }
            else if (!_isSignaled)
            {
                _isSignaled = true;
            }
        }

        toRelease?.Release();
    }

    public async ValueTask WaitAsync()
    {
        if (CheckSignaled())
        {
            return;
        }

        SemaphoreSlim s;
        lock (_queue)
        {
            _queue.Enqueue(s = new SemaphoreSlim(initialCount: 0, maxCount: 1));
        }

        await s.WaitAsync();
        lock (_queue)
        {
            if (_queue.Count > 0 && _queue.Peek() == s)
            {
                _queue.Dequeue().Dispose();
            }
        }
    }

    public async ValueTask WaitAsync(int millisecondsTimeout)
    {
        if (CheckSignaled())
        {
            return;
        }

        SemaphoreSlim s;
        lock (_queue)
        {
            _queue.Enqueue(s = new SemaphoreSlim(initialCount: 0, maxCount: 1));
        }

        await s.WaitAsync(millisecondsTimeout);
        lock (_queue)
        {
            if (_queue.Count > 0 && _queue.Peek() == s)
            {
                _queue.Dequeue().Dispose();
            }
        }
    }

    public async ValueTask WaitAsync(int millisecondsTimeout, CancellationToken cancellationToken)
    {
        if (CheckSignaled())
        {
            return;
        }

        SemaphoreSlim s;
        lock (_queue)
        {
            _queue.Enqueue(s = new SemaphoreSlim(initialCount: 0, maxCount: 1));
        }

        try
        {
            await s.WaitAsync(millisecondsTimeout, cancellationToken);
        }
        finally
        {
            lock (_queue)
            {
                if (_queue.Count > 0 && _queue.Peek() == s)
                {
                    _queue.Dequeue().Dispose();
                }
            }
        }
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        if (CheckSignaled())
        {
            return;
        }

        SemaphoreSlim s;
        lock (_queue)
        {
            _queue.Enqueue(s = new SemaphoreSlim(initialCount: 0, maxCount: 1));
        }

        try
        {
            await s.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_queue)
            {
                if (_queue.Count > 0 && _queue.Peek() == s)
                {
                    _queue.Dequeue().Dispose();
                }
            }
        }
    }

    public async ValueTask WaitAsync(TimeSpan timeout)
    {
        if (CheckSignaled())
        {
            return;
        }

        SemaphoreSlim s;
        lock (_queue)
        {
            _queue.Enqueue(s = new SemaphoreSlim(initialCount: 0, maxCount: 1));
        }

        await s.WaitAsync(timeout);
        lock (_queue)
        {
            if (_queue.Count > 0 && _queue.Peek() == s)
            {
                _queue.Dequeue().Dispose();
            }
        }
    }

    public async ValueTask WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (CheckSignaled())
        {
            return;
        }

        SemaphoreSlim s;
        lock (_queue)
        {
            _queue.Enqueue(s = new SemaphoreSlim(initialCount: 0, maxCount: 1));
        }

        try
        {
            await s.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            lock (_queue)
            {
                if (_queue.Count > 0 && _queue.Peek() == s)
                {
                    _queue.Dequeue().Dispose();
                }
            }
        }
    }

    private bool CheckSignaled()
    {
        lock (_queue)
        {
            if (_isSignaled)
            {
                _isSignaled = false;
                return true;
            }

            return false;
        }
    }
}