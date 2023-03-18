using Microsoft.Win32.SafeHandles;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace DiscordFS.Platforms.Windows.Helpers;

public class SafeTransferKey : IDisposable
{
    private readonly CF_TRANSFER_KEY _transferKey;
    private readonly HFILE _handle;

    public SafeTransferKey(HFILE handle)
    {
        _handle = handle;

        CfGetTransferKey(_handle, out _transferKey).ThrowIfFailed();
    }

    public SafeTransferKey(SafeFileHandle safeHandle)
    {
        _handle = safeHandle;

        CfGetTransferKey(_handle, out _transferKey).ThrowIfFailed();
    }


    public static implicit operator CF_TRANSFER_KEY(SafeTransferKey instance)
    {
        return instance._transferKey;
    }


    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing) { }

            if (!_handle.IsInvalid)
            {
                CfReleaseTransferKey(_handle, _transferKey);
            }

            _disposed = true;
        }
    }

    ~SafeTransferKey()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}