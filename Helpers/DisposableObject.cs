namespace DiscordFS.Storage;

public class DisposableObject<T> : IDisposable
{
    public T Value { get; }

    private readonly Action<T> _disposeAction;
    private bool _disposedValue;

    public DisposableObject(Action<T> disposeAction, T value)
    {
        Value = value;
        _disposeAction = disposeAction;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _disposeAction?.Invoke(Value);
            }

            _disposedValue = true;
        }
    }
}