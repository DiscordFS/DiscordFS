using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace DiscordFS.Platforms.Windows.Helpers;

public class SafeOpenFileWithOplock : IDisposable
{
    public bool IsInvalid
    {
        get { return _handle.IsInvalid; }
    }

    private readonly SafeHCFFILE _handle;


    private bool _disposedValue;

    public SafeOpenFileWithOplock(string fullPath, CF_OPEN_FILE_FLAGS flags)
    {
        var result = CfOpenFileWithOplock(fullPath, flags, out _handle);
        result.ThrowIfFailed();
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public static implicit operator SafeHCFFILE(SafeOpenFileWithOplock instance)
    {
        return instance._handle;
    }

    public static implicit operator HFILE(SafeOpenFileWithOplock instance)
    {
        return instance._handle.DangerousGetHandle();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _handle?.Dispose();
            }

            _disposedValue = true;
        }
    }
}