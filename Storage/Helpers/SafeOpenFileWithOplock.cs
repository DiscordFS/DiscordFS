using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace DiscordFS.Storage.Helpers;

public class SafeOpenFileWithOplock : IDisposable
{
    public SafeOpenFileWithOplock(string fullPath, CF_OPEN_FILE_FLAGS Flags)
    {
        CfOpenFileWithOplock(fullPath, Flags, out _handle);
    }

    public bool IsInvalid
    {
        get { return _handle.IsInvalid; }
    }

    public static implicit operator SafeHCFFILE(SafeOpenFileWithOplock instance)
    {
        return instance._handle;
    }

    public static implicit operator HFILE(SafeOpenFileWithOplock instance)
    {
        return instance._handle.DangerousGetHandle();
    }

    private readonly SafeHCFFILE _handle;


    private bool _disposedValue;

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

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}