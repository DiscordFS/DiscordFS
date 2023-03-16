using System.Diagnostics;
using System.Runtime.InteropServices;
using DiscordFS.Platforms.Windows.Helpers;
using DiscordFS.Storage.FileSystem.Results;
using Vanara.Extensions;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace DiscordFS.Platforms.Windows.Storage;

public class ExtendedPlaceholderState : IDisposable
{
    public string FullPath { get; }

    public bool IsDirectory
    {
        get { return Attributes.HasFlag(FileAttributes.Directory); }
    }

    public bool IsPlaceholder
    {
        get { return PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PLACEHOLDER); }
    }

    public CF_PLACEHOLDER_STANDARD_INFO PlaceholderInfoStandard
    {
        get
        {
            if (_placeholderInfoStandard == null)
            {
                if (string.IsNullOrEmpty(FullPath))
                {
                    _placeholderInfoStandard = new CF_PLACEHOLDER_STANDARD_INFO();
                }
                else if (!PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PLACEHOLDER))
                {
                    _placeholderInfoStandard = new CF_PLACEHOLDER_STANDARD_INFO();
                }
                else
                {
                    _placeholderInfoStandard = GetPlaceholderInfoStandard(SafeFileHandleForCldApi);
                }
            }

            return _placeholderInfoStandard.Value;
        }
    }

    public HFILE SafeFileHandleForCldApi
    {
        get
        {
            if (_safeFileHandleForCldApi == null)
            {
                _safeFileHandleForCldApi = new SafeCreateFileForCldApi(FullPath, IsDirectory);
            }

            return _safeFileHandleForCldApi;
        }
    }

    private bool _disposedValue;
    private WIN32_FIND_DATA _findData;
    private CF_PLACEHOLDER_STANDARD_INFO? _placeholderInfoStandard;
    private SafeCreateFileForCldApi _safeFileHandleForCldApi;
    public FileAttributes Attributes;

    public string ETag;

    // Fake ID.
    public string FileId = Guid.NewGuid().ToString();
    public ulong FileSize;
    public DateTime LastWriteTime;

    public CF_PLACEHOLDER_STATE PlaceholderState;

    public ExtendedPlaceholderState(string fullPath)
    {
        FullPath = fullPath;

        using var findHandle = Kernel32.FindFirstFile(@"\\?\" + FullPath, out var findData);
        SetValuesByFindData(findData);
    }

    public ExtendedPlaceholderState(WIN32_FIND_DATA findData, string directory)
    {
        if (!string.IsNullOrEmpty(directory))
        {
            FullPath = directory + "\\" + findData.cFileName;
        }

        _findData = findData;

        SetValuesByFindData(findData);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public FileOperationResult ConvertToPlaceholder(bool markInSync)
    {
        if (string.IsNullOrEmpty(FullPath))
        {
            return new FileOperationResult(CloudFilterNTStatus.STATUS_UNSUCCESSFUL);
        }

        if (IsPlaceholder)
        {
            return new FileOperationResult(CloudFilterNTStatus.STATUS_SUCCESS);
        }

        using var fHandle = new SafeOpenFileWithOplock(FullPath, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE);

        if (fHandle.IsInvalid)
        {
            return new FileOperationResult(CloudFilterNTStatus.STATUS_UNSUCCESSFUL);
        }

        var flags = markInSync
            ? CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC
            : CF_CONVERT_FLAGS.CF_CONVERT_FLAG_ENABLE_ON_DEMAND_POPULATION;

        HRESULT res;
        var fileIdSize = FileId.Length * Marshal.SizeOf(FileId[index: 0]);

        unsafe
        {
            fixed (void* fileId = FileId)
            {
                res = CfConvertToPlaceholder(fHandle, (nint)fileId, (uint)fileIdSize, flags, out var usn);
            }
        }

        if (res.Succeeded)
        {
            Reload();
        }

        return new FileOperationResult(res.Succeeded);
    }

    public FileOperationResult DehydratePlaceholder(bool setPinStateUnspecified)
    {
        if (!IsPlaceholder)
        {
            return new FileOperationResult(CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE);
        }

        if (PlaceholderInfoStandard.PinState == CF_PIN_STATE.CF_PIN_STATE_PINNED)
        {
            return new FileOperationResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_PINNED);
        }

        Debug.WriteLine("DehydratePlaceholder " + FullPath, TraceLevel.Verbose);

        var res = CfDehydratePlaceholder(SafeFileHandleForCldApi, StartingOffset: 0, Length: -1, CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE);
        if (res.Succeeded)
        {
            Reload();
        }
        else
        {
            Debug.WriteLine("DehydratePlaceholder FAILED" + FullPath + " Error: " + res.Code, TraceLevel.Warning);
            return new FileOperationResult((int)res);
        }

        if (res.Succeeded && setPinStateUnspecified)
        {
            SetPinState(CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED);
        }

        return new FileOperationResult((int)res);
    }

    public bool DisableOnDemandPopulation()
    {
        if (string.IsNullOrEmpty(FullPath))
        {
            return false;
        }

        if (!IsPlaceholder)
        {
            return false;
        }

        if (!IsDirectory)
        {
            return false;
        }

        if (!PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL))
        {
            return true;
        }

        Debug.WriteLine("EnableOnDemandPopulation " + FullPath, TraceLevel.Verbose);

        using var fHandle = new SafeOpenFileWithOplock(FullPath, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_NONE);

        if (fHandle.IsInvalid)
        {
            return false;
        }

        HRESULT res;

        var fileIdSize = FileId.Length * Marshal.SizeOf(FileId[index: 0]);
        long usn = 0;

        unsafe
        {
            fixed (void* fileId = FileId)
            {
                res = CfUpdatePlaceholder(fHandle, new CF_FS_METADATA(), (nint)fileId, (uint)fileIdSize, DehydrateRangeArray: null,
                    DehydrateRangeCount: 0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DISABLE_ON_DEMAND_POPULATION, ref usn);
            }
        }

        if (res.Succeeded)
        {
            //Reload of Placeholder after EnableOnDemandPopulation triggers FETCH_PLACEHOLDERS  
            //Reload();
            PlaceholderState ^= CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL;
            PlaceholderState ^= CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIALLY_ON_DISK;
        }

        return res.Succeeded;
    }

    public bool EnableOnDemandPopulation()
    {
        if (string.IsNullOrEmpty(FullPath))
        {
            return false;
        }

        if (!IsPlaceholder)
        {
            return false;
        }

        if (!IsDirectory)
        {
            return false;
        }

        if (PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL))
        {
            return true;
        }

        Debug.WriteLine("EnableOnDemandPopulation " + FullPath, TraceLevel.Verbose);

        using var fHandle = new SafeOpenFileWithOplock(FullPath, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_NONE);

        if (fHandle.IsInvalid)
        {
            Debug.WriteLine(format: "EnableOnDemandPopulation FAILED: Invalid Handle!", TraceLevel.Warning);
            return false;
        }

        HRESULT res;

        var fileIdSize = FileId.Length * Marshal.SizeOf(FileId[index: 0]);
        long usn = 0;

        unsafe
        {
            fixed (void* fileId = FileId)
            {
                res = CfUpdatePlaceholder(fHandle, new CF_FS_METADATA(), (nint)fileId, (uint)fileIdSize, DehydrateRangeArray: null,
                    DehydrateRangeCount: 0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_ENABLE_ON_DEMAND_POPULATION, ref usn);
            }
        }

        if (res.Succeeded)
        {
            //Reload of Placeholder after EnableOnDemandPopulation triggers FETCH_PLACEHOLDERS  
            //Reload();
            PlaceholderState |= CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL
                                | CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIALLY_ON_DISK;
        }
        else
        {
            Debug.WriteLine("ConvertToPlaceholder FAILED: Error " + res.Code, TraceLevel.Warning);
        }

        return res.Succeeded;
    }

    public FileOperationResult HydratePlaceholder()
    {
        if (string.IsNullOrEmpty(FullPath))
        {
            return new FileOperationResult(CloudFileFetchErrorCode.FileOrDirectoryNotFound);
        }

        if (!IsPlaceholder)
        {
            return new FileOperationResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_NOT_SUPPORTED);
        }

        Debug.WriteLine("HydratePlaceholder " + FullPath, TraceLevel.Verbose);

        var res = CfHydratePlaceholder(SafeFileHandleForCldApi);

        if (res.Succeeded)
        {
            Reload();
            return new FileOperationResult();
        }

        Debug.WriteLine("HydratePlaceholder FAILED " + FullPath + " Error: " + res.Code, TraceLevel.Warning);
        return new FileOperationResult((int)res);
    }

    public async Task<FileOperationResult> HydratePlaceholderAsync()
    {
        if (string.IsNullOrEmpty(FullPath))
        {
            return new FileOperationResult(CloudFileFetchErrorCode.FileOrDirectoryNotFound);
        }

        if (!IsPlaceholder)
        {
            return new FileOperationResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_NOT_SUPPORTED);
        }

        Debug.WriteLine("HydratePlaceholderAsync " + FullPath, TraceLevel.Info);


        var res = await Task.Run(() =>
            {
                return CfHydratePlaceholder(SafeFileHandleForCldApi);
            })
            .ConfigureAwait(continueOnCapturedContext: false);

        if (res.Succeeded)
        {
            Debug.WriteLine("HydratePlaceholderAsync Completed: " + FullPath, TraceLevel.Verbose);
            Reload();
            return new FileOperationResult();
        }

        Debug.WriteLine("HydratePlaceholderAsync FAILED " + FullPath + " Error: " + res.Code, TraceLevel.Warning);
        return new FileOperationResult((int)res);
    }


    public void Reload()
    {
        using var findHandle = Kernel32.FindFirstFile(@"\\?\" + FullPath, out var findData);
        SetValuesByFindData(findData);
    }

    public FileOperationResult RevertPlaceholder(bool allowDataLoos)
    {
        if (string.IsNullOrEmpty(FullPath))
        {
            return new FileOperationResult(CloudFileFetchErrorCode.FileOrDirectoryNotFound);
        }

        if (!IsPlaceholder)
        {
            return new FileOperationResult
            {
                Status = CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE,
                Message = CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE.ToString(),
                Succeeded = true
            };
        }

        using var fHandle = new SafeOpenFileWithOplock(FullPath, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE);
        if (fHandle.IsInvalid)
        {
            return new FileOperationResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_IN_USE);
        }

        if (!allowDataLoos)
        {
            var ret = HydratePlaceholder();
            if (!ret.Succeeded)
            {
                return ret;
            }

            if (PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL) ||
                PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_INVALID) ||
                !PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC))
            {
                return new FileOperationResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_NOT_IN_SYNC);
            }
        }

        var res = CfRevertPlaceholder(fHandle, CF_REVERT_FLAGS.CF_REVERT_FLAG_NONE);

        if (res.Succeeded)
        {
            Reload();
        }
        else
        {
            Debug.WriteLine("RevertPlaceholder FAILED: Error " + res.Code, TraceLevel.Warning);
        }

        return new FileOperationResult((int)res);
    }

    public FileOperationResult SetInSyncState(CF_IN_SYNC_STATE inSyncState)
    {
        if (!IsPlaceholder)
        {
            return new FileOperationResult(CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE);
        }

        if (PlaceholderInfoStandard.InSyncState == inSyncState)
        {
            return new FileOperationResult();
        }

        var res = CfSetInSyncState(SafeFileHandleForCldApi, inSyncState, CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE);
        if (res.Succeeded)
        {
            //Reload();

            // Prevent reload by applying results directly to cached values:
            if (_placeholderInfoStandard != null)
            {
                var p = _placeholderInfoStandard.Value;
                p.InSyncState = inSyncState;
                _placeholderInfoStandard = p;
            }

            if (inSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC)
            {
                PlaceholderState |= CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC;
            }
            else
            {
                PlaceholderState ^= CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC;
            }


            return new FileOperationResult();
        }

        return new FileOperationResult((int)res);
    }

    public bool SetPinState(CF_PIN_STATE state)
    {
        if (!IsPlaceholder)
        {
            return false;
        }

        if (((int)PlaceholderInfoStandard.PinState) == ((int)state))
        {
            return true;
        }

        Debug.WriteLine("SetPinState " + FullPath + " " + state, TraceLevel.Verbose);
        var res = CfSetPinState(SafeFileHandleForCldApi, state, CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE);

        if (res.Succeeded)
        {
            //Reload();

            // Prevent reload by applying results directly to cached values:
            if (_placeholderInfoStandard != null)
            {
                var p = _placeholderInfoStandard.Value;
                p.PinState = state;
                _placeholderInfoStandard = p;
            }
        }
        else
        {
            Debug.WriteLine("SetPinState FAILED " + FullPath + " Error: " + res.Code, TraceLevel.Warning);
        }

        return res.Succeeded;
    }

    public FileOperationResult UpdatePlaceholder(FilePlaceholder placeholder, CF_UPDATE_FLAGS cFUpdateFlags)
    {
        return UpdatePlaceholder(placeholder, cFUpdateFlags, markDataInvalid: false);
    }

    public FileOperationResult UpdatePlaceholder(
        FilePlaceholder placeholder,
        CF_UPDATE_FLAGS cFUpdateFlags,
        bool markDataInvalid)
    {
        if (string.IsNullOrEmpty(FullPath))
        {
            return new FileOperationResult(CloudFileFetchErrorCode.FileOrDirectoryNotFound);
        }

        if (!IsPlaceholder)
        {
            return new FileOperationResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_NOT_SUPPORTED);
        }

        var res = new FileOperationResult();

        using var fHandle = new SafeOpenFileWithOplock(FullPath, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE);

        if (fHandle.IsInvalid)
        {
            return new FileOperationResult(CloudFilterNTStatus.STATUS_CLOUD_FILE_IN_USE);
        }

        var fileIdSize = FileId.Length * Marshal.SizeOf(FileId[index: 0]);
        long usn = 0;

        unsafe
        {
            fixed (void* fileId = FileId)
            {
                CF_FILE_RANGE[] dehydrateRanges = null;
                uint dehydrateRangesCount = 0;
                if (markDataInvalid)
                {
                    dehydrateRanges = new CF_FILE_RANGE[1];
                    dehydrateRanges[0] = new CF_FILE_RANGE { StartingOffset = 0, Length = (long)FileSize };
                    dehydrateRangesCount = 1;
                }

                var res1 = CfUpdatePlaceholder(fHandle, CreateFSMetaData(placeholder), (nint)fileId, (uint)fileIdSize, dehydrateRanges,
                    dehydrateRangesCount, cFUpdateFlags, ref usn);
                if (!res1.Succeeded)
                {
                    res.SetException(res1.GetException());
                }
            }
        }

        if (res.Succeeded)
        {
            Reload();
        }

        return res;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _safeFileHandleForCldApi?.Dispose();
            }

            _disposedValue = true;
        }
    }

    private Kernel32.FILE_BASIC_INFO CreateFileBasicInfo(BasicFileInfo placeholder)
    {
        return new Kernel32.FILE_BASIC_INFO
        {
            FileAttributes = (FileFlagsAndAttributes)placeholder.FileAttributes,
            CreationTime = placeholder.CreationTime.ToFileTimeStruct(),
            LastWriteTime = placeholder.LastWriteTime.ToFileTimeStruct(),
            LastAccessTime = placeholder.LastAccessTime.ToFileTimeStruct(),
            ChangeTime = placeholder.LastWriteTime.ToFileTimeStruct()
        };
    }

    private CF_FS_METADATA CreateFSMetaData(FilePlaceholder placeholder)
    {
        return new CF_FS_METADATA
        {
            FileSize = placeholder.FileSize,
            BasicInfo = CreateFileBasicInfo(placeholder)
        };
    }

    private CF_PLACEHOLDER_STANDARD_INFO GetPlaceholderInfoStandard(string fullPath, bool isDirectory)
    {
        using var handle = new SafeCreateFileForCldApi(fullPath, isDirectory);

        if (handle.IsInvalid)
        {
            return default;
        }

        try
        {
            return GetPlaceholderInfoStandard(handle);
        }
        catch (Exception e)
        {
            return default;
        }
    }

    private CF_PLACEHOLDER_STANDARD_INFO GetPlaceholderInfoStandard(HFILE fileHandle)
    {
        if (fileHandle.IsInvalid)
        {
            return default;
        }

        try
        {
            const int infoBufferLength = 1024;
            CF_PLACEHOLDER_STANDARD_INFO resultInfo = default;

            using var bufferPointerHandler = new SafeAllocCoTaskMem(infoBufferLength);

            CfGetPlaceholderInfo(fileHandle, CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_STANDARD, bufferPointerHandler,
                infoBufferLength, out var returnedLength);

            if (returnedLength > 0)
            {
                resultInfo = Marshal.PtrToStructure<CF_PLACEHOLDER_STANDARD_INFO>(bufferPointerHandler);
            }

            return resultInfo;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private void SetValuesByFindData(WIN32_FIND_DATA findData)
    {
        PlaceholderState = CfGetPlaceholderStateFromFindData(findData);
        Attributes = findData.dwFileAttributes;
        LastWriteTime = findData.ftLastWriteTime.ToDateTime();
        _placeholderInfoStandard = null;
        FileSize = findData.FileSize;
        ETag = "_" + LastWriteTime.ToUniversalTime().Ticks + "_" + FileSize;
    }
}