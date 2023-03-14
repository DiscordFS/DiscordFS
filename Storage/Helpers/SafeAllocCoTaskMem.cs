using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Microsoft.Win32.SafeHandles;
using Vanara.PInvoke;

namespace DiscordFS.Storage.Helpers;

public class SafeCreateFileForCldApi : IDisposable
{
    public SafeCreateFileForCldApi(string fullPath, bool isDirectory)
    {
        var accessFlag = isDirectory
            ? FILE_ACCESS_FLAGS.FILE_GENERIC_READ
            : FILE_ACCESS_FLAGS.FILE_READ_EA;
        var attributsFlag = isDirectory
            ? FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS
            : FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED;

        _handle = PInvoke.CreateFileW(@"\\?\" + fullPath,
            accessFlag,
            FILE_SHARE_MODE.FILE_SHARE_READ |
            FILE_SHARE_MODE.FILE_SHARE_WRITE |
            FILE_SHARE_MODE.FILE_SHARE_DELETE,
            lpSecurityAttributes: null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            attributsFlag,
            hTemplateFile: null);
    }

    public bool IsInvalid
    {
        get { return _handle.IsInvalid; }
    }

    public static implicit operator SafeFileHandle(SafeCreateFileForCldApi instance)
    {
        return instance._handle;
    }

    public static implicit operator HFILE(SafeCreateFileForCldApi instance)
    {
        return instance._handle;
    }

    private readonly SafeFileHandle _handle;

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

public class SafeAllocCoTaskMem : IDisposable
{
    private readonly nint _pointer;
    public readonly int Size;

    public SafeAllocCoTaskMem(int size)
    {
        Size = size;
        _pointer = Marshal.AllocCoTaskMem(Size);
    }

    public SafeAllocCoTaskMem(object structure)
    {
        Size = Marshal.SizeOf(structure);
        _pointer = Marshal.AllocCoTaskMem(Size);
        Marshal.StructureToPtr(structure, _pointer, fDeleteOld: false);
    }

    public SafeAllocCoTaskMem(string data)
    {
        Size = data.Length * Marshal.SystemDefaultCharSize;
        _pointer = Marshal.StringToCoTaskMemUni(data);
    }

    public static implicit operator nint(SafeAllocCoTaskMem instance)
    {
        return instance._pointer;
    }

    private bool _disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing) { }

            Marshal.FreeCoTaskMem(_pointer);
            _disposedValue = true;
        }
    }

    ~SafeAllocCoTaskMem()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}