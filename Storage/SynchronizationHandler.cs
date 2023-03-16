using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Tasks.Dataflow;
using DiscordFS.Helpers;
using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.Actions;
using DiscordFS.Storage.Files;
using DiscordFS.Storage.Files.Results;
using DiscordFS.Storage.Helpers;
using DiscordFS.Storage.Synchronization;
using Microsoft.Extensions.Logging;
using Vanara.Extensions;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace DiscordFS.Storage;

public class SynchronizationHandler : IDisposable
{
    // 2 MB chunk size for File Download / Upload
    private const int ChunkSize = 1024 * 1024 * 2;

    private static readonly TimeSpan FailedQueueTimerInterval = TimeSpan.FromSeconds(value: 30);
    private static readonly TimeSpan LocalSyncTimerInterval = TimeSpan.FromSeconds(value: 30);

    private readonly CF_CALLBACK_REGISTRATION[] _callbackMappings;
    private readonly ActionBlock<DeleteAction> _deleteQueue;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _fetchPlaceholdersCancellationTokens;
    private readonly ConcurrentDictionary<string, FailedDataSynchronization> _failedDataQueue;

    private readonly IRemoteFileSystemProvider _fileSystemProvider;
    private readonly FileRangeManager _fileRangeManager;
    private readonly LockableQueue<string> _localChangedDataQueue;
    private readonly LockableQueue<string> _remoteChangedDataQueue;
    private readonly ILogger _logger;
    private readonly StorageProviderOptions _options;

    private CancellationTokenSource _changedDataCancellationTokenSource;
    private CF_CONNECTION_KEY? _connectionKey;
    private bool _disposed;
    private FileSystemWatcher _watcher;
    private readonly Timer _localSyncTimer;

    public bool SyncInProgress { get; protected set; }

    private readonly Timer _failedQueueTimer;
    private readonly ActionBlock<ProcessChangedDataArgs> _changedDataQueue;
    private readonly ActionBlock<SyncDataParam> _syncActionBlock;

    public SynchronizationHandler(
        FileRangeManager fileRangeManager,
        IRemoteFileSystemProvider fileSystemProvider,
        StorageProviderOptions options,
        ILogger logger)
    {
        _fileSystemProvider = fileSystemProvider;
        _options = options;
        _logger = logger;
        _fileRangeManager = fileRangeManager;
        _deleteQueue = new ActionBlock<DeleteAction>(NotifyDeleteAction);
        _failedDataQueue = new ConcurrentDictionary<string, FailedDataSynchronization>();

        _remoteChangedDataQueue = new LockableQueue<string>();
        _localChangedDataQueue = new LockableQueue<string>();
        _fileSystemProvider.StateChange += OnFileSystemProviderStateChange;

        _callbackMappings = new[]
        {
            new()
            {
                Callback = CbFetchPlaceHolders,
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS
            },
            new()
            {
                Callback = CbCancelFetchPlaceHolders,
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_PLACEHOLDERS
            },
            new()
            {
                Callback = CbFetchData,
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA
            },
            new()
            {
                Callback = CbCancelFetchData,
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA
            },
            new()
            {
                Callback = CbNotifyFileOpenCompletion,
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION
            },
            new()
            {
                Callback = CbNotifyFileCloseCompletion,
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION
            },
            new()
            {
                Callback = CbNotifyDelete,
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE
            },
            new()
            {
                Callback = CbNotifyDeleteCompletion,
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE_COMPLETION
            },
            new()
            {
                Callback = CbNotifyRename,
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME
            },
            new()
            {
                Callback = CbNotifyRenameCompletion,
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME_COMPLETION
            },
            CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
        };

        _fetchPlaceholdersCancellationTokens = new ConcurrentDictionary<string, CancellationTokenSource>();

        _failedQueueTimer = new Timer(FailedQueueTimerCallback, state: null, Timeout.Infinite, Timeout.Infinite);
        _localSyncTimer = new Timer(LocalSyncTimerCallback, state: null, Timeout.Infinite, Timeout.Infinite);

        _syncActionBlock = new ActionBlock<SyncDataParam>(SyncAction);
        _changedDataQueue =
            new ActionBlock<ProcessChangedDataArgs>(ChangedDataAction, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1 });
    }

    public void OnFileSystemProviderStateChange(object sender, FileProviderStateChangedEventArgs e)
    {
        if (e.Status == FileProviderStatus.Ready)
        {
            _localSyncTimer.Change(LocalSyncTimerInterval, LocalSyncTimerInterval);
            _failedQueueTimer.Change(FailedQueueTimerInterval, FailedQueueTimerInterval);
        }
        else
        {
            _localSyncTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _failedQueueTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private Task SyncAction(SyncDataParam data)
    {
        return SyncDataAsyncRecursive(data.Folder, data.CancellationToken, data.SyncMode);
    }

    private async Task ProcessChangedDataAsync2(
        string fullPath,
        ExtendedPlaceholderState localPlaceHolder,
        DynamicServerPlaceholder dynamicRemotePlaceHolder,
        SyncMode syncMode,
        CancellationToken ctx)
    {
        try
        {
            var relativePath = PathHelper.GetRelativePath(fullPath, _options.LocalPath);

            // Convert to placeholder if required
            if (!localPlaceHolder.ConvertToPlaceholder(markInSync: false))
            {
                throw new Exception(message: "Convert to Placeholder failed");
            }

            // Ignore special files.
            if (FileExcluder.IsExcludedFile(fullPath, localPlaceHolder.Attributes))
            {
                localPlaceHolder.SetPinState(CF_PIN_STATE.CF_PIN_STATE_EXCLUDED);
                localPlaceHolder.SetInSyncState(CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);
            }
            else if (localPlaceHolder.PlaceholderInfoStandard.PinState == CF_PIN_STATE.CF_PIN_STATE_EXCLUDED)
            {
                localPlaceHolder.SetPinState(CF_PIN_STATE.CF_PIN_STATE_INHERIT);
            }

            if (localPlaceHolder.PlaceholderInfoStandard.PinState == CF_PIN_STATE.CF_PIN_STATE_EXCLUDED)
            {
                return;
            }

            if (localPlaceHolder.IsDirectory)
            {
                if (syncMode == SyncMode.Full)
                {
                    if ((await dynamicRemotePlaceHolder.GetPlaceholderAsync()) == null)
                    {
                        // Directory does not exist on Server
                        if (localPlaceHolder.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC))
                        {
                            // Directory remotely deleted if it was in sync.
                            return;
                        }

                        // File locally created or modified while deleted on Server
                        var creatResult = await _fileSystemProvider.Operations.CreateFileAsync(relativePath, isDirectory: true);
                        creatResult.ThrowOnFailure();

                        localPlaceHolder.UpdatePlaceholder(creatResult.FilePlaceholder, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC)
                            .ThrowOnFailure();
                    }
                }
            }
            else
            {
                // Compare with remote file if Full Sync
                if (syncMode == SyncMode.Full)
                {
                    if ((await dynamicRemotePlaceHolder.GetPlaceholderAsync()) == null)

                        // New local file or remote deleted File
                    {
                        // File does not exist on Server
                        if (localPlaceHolder.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC))
                        {
                            // File remotely deleted if it was in sync.
                            MoveToRecycleBin(localPlaceHolder);
                            return;
                        }

                        // File locally created or modified while deleted on Server
                        var uploadFileToServerResult = await _fileSystemProvider.UploadFileAsync(fullPath, ctx);
                        uploadFileToServerResult.ThrowOnFailure();
                        return;
                    }

                    // Validate ETag
                    ValidateETag(localPlaceHolder, (await dynamicRemotePlaceHolder.GetPlaceholderAsync()));
                }

                // local file full populated and out of sync
                if (localPlaceHolder.PlaceholderInfoStandard.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC &&
                    !localPlaceHolder.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL))
                {
                    // Local File changed: Upload to Server
                    var remotePlaceHolder = await dynamicRemotePlaceHolder.GetPlaceholderAsync();
                    if (remotePlaceHolder == null || (localPlaceHolder.LastWriteTime > remotePlaceHolder.LastWriteTime))
                    {
                        await _fileSystemProvider.UploadFileAsync(fullPath, ctx);
                        localPlaceHolder.Reload();
                    }
                    else
                    {
                        // Local File requires update...
                        if (localPlaceHolder.PlaceholderInfoStandard.PinState == CF_PIN_STATE.CF_PIN_STATE_PINNED)
                        {
                            HydratePlaceholder(localPlaceHolder, await dynamicRemotePlaceHolder.GetPlaceholderAsync());
                            return;
                        }

                        //Backup local file, Dehydrate and update placeholder
                        if (!localPlaceHolder.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL))
                        {
                            await CreatePreviousVersionAsync(fullPath, ctx);
                        }

                        localPlaceHolder.UpdatePlaceholder(await dynamicRemotePlaceHolder.GetPlaceholderAsync(),
                                CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE)
                            .ThrowOnFailure();

                        return;
                    }
                }

                // Dehydration requested
                if (localPlaceHolder.PlaceholderInfoStandard.PinState == CF_PIN_STATE.CF_PIN_STATE_UNPINNED)
                {
                    await DehydratePlaceholderAsync(localPlaceHolder, await dynamicRemotePlaceHolder.GetPlaceholderAsync(), ctx);
                    return;
                }

                // Hydration requested
                if (localPlaceHolder.PlaceholderInfoStandard.PinState == CF_PIN_STATE.CF_PIN_STATE_PINNED
                    && localPlaceHolder.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL))
                {
                    HydratePlaceholder(localPlaceHolder, await dynamicRemotePlaceHolder.GetPlaceholderAsync());
                    return;
                }

                // local file not fully populated and out of sync -> Update and Dehydrate
                if (localPlaceHolder.PlaceholderInfoStandard.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC &&
                    localPlaceHolder.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL))
                {
                    localPlaceHolder.UpdatePlaceholder(await dynamicRemotePlaceHolder.GetPlaceholderAsync(),
                            CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC)
                        .ThrowOnFailure();

                    return;
                }

                // Info if placeholder is still not in sync
                // TODO: Retry at a later time.
                if (localPlaceHolder.PlaceholderInfoStandard.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC)
                {
                    unchecked
                    {
                        throw new Win32Exception((int)CloudFilterNTStatus.STATUS_CLOUD_FILE_NOT_IN_SYNC,
                            $"Not in sync after processing: {fullPath}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, message: null);
        }
    }

    [SupportedOSPlatform(platformName: "windows")]
    private void MoveToRecycleBin(ExtendedPlaceholderState localPlaceHolder)
    {
        var relativePath = PathHelper.GetRelativePath(localPlaceHolder.FullPath, _options.LocalPath);
        var recyclePath = Path.Combine(_options.LocalPath, path2: "$Recycle.bin", relativePath);
        var recycleDirectory = Path.GetDirectoryName(recyclePath);

        if (!localPlaceHolder.IsPlaceholder)
        {
            return;
        }

        localPlaceHolder.SetPinState(CF_PIN_STATE.CF_PIN_STATE_EXCLUDED);
        localPlaceHolder.SetInSyncState(CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);

        if (localPlaceHolder.IsDirectory)
        {
            // TODO: Delete Directory....
            return;
        }

        if (localPlaceHolder.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL))
        {
            //localPlaceHolder.RevertPlaceholder(true);
            File.Delete(localPlaceHolder.FullPath);
            return;
        }

        localPlaceHolder.RevertPlaceholder(allowDataLoos: false).ThrowOnFailure();

        if (!Directory.Exists(recycleDirectory))
        {
            Directory.CreateDirectory(recycleDirectory!);
        }

        File.Move(localPlaceHolder.FullPath, recyclePath);
    }

    // ReSharper disable once UnusedParameter.Local
    private Task CreatePreviousVersionAsync(string fullPath, CancellationToken ctx)
    {
        //todo: implement this
        return Task.CompletedTask;
    }

    private void ValidateETag(ExtendedPlaceholderState localPlaceHolder, FilePlaceholder remotePlaceholder)
    {
        if (localPlaceHolder?.ETag != remotePlaceholder?.ETag || localPlaceHolder?.PlaceholderInfoStandard.ModifiedDataSize > 0)
        {
            localPlaceHolder?.SetInSyncState(CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC).ThrowOnFailure();
            return;
        }

        if (localPlaceHolder?.PlaceholderInfoStandard.ModifiedDataSize == 0)
        {
            localPlaceHolder.SetInSyncState(CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC).ThrowOnFailure();
        }
    }

    private async Task DehydratePlaceholderAsync(
        ExtendedPlaceholderState localPlaceHolder,
        FilePlaceholder remotePlaceholder,
        CancellationToken ctx)
    {
        if (localPlaceHolder.PlaceholderInfoStandard.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC)
        {
            // Local file in Sync: Dehydrate
            localPlaceHolder.DehydratePlaceholder(setPinStateUnspecified: false).ThrowOnFailure();
        }
        else
        {
            if (localPlaceHolder.LastWriteTime <= remotePlaceholder.LastWriteTime)
            {
                // Local file older: Dehydrate and update MetaData
                localPlaceHolder.UpdatePlaceholder(remotePlaceholder,
                        CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE |
                        CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC)
                    .ThrowOnFailure();
            }
            else
            {
                // Local file newer than Server: Upload, dehydrate, update MetaData
                if (!localPlaceHolder.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL))
                {
                    // Upload if local file is fully available
                    await _fileSystemProvider.UploadFileAsync(localPlaceHolder.FullPath, ctx);
                    localPlaceHolder.Reload();
                }

                localPlaceHolder.UpdatePlaceholder(remotePlaceholder,
                        CF_UPDATE_FLAGS.CF_UPDATE_FLAG_VERIFY_IN_SYNC |
                        CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE |
                        CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC)
                    .ThrowOnFailure();
            }
        }

        localPlaceHolder.SetPinState(CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED);
    }

    private void HydratePlaceholder(ExtendedPlaceholderState localPlaceHolder, FilePlaceholder remotePlaceholder)
    {
        if (localPlaceHolder.PlaceholderInfoStandard.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC)
        {
            // Local File in Sync: Hydrate....
            localPlaceHolder.HydratePlaceholder().ThrowOnFailure();
            return;
        }

        var pinned = (localPlaceHolder.PlaceholderInfoStandard.PinState == CF_PIN_STATE.CF_PIN_STATE_PINNED);
        if (pinned)
        {
            localPlaceHolder.SetPinState(CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED);
        }

        // Local File not in Sync: Update placeholder, dehydrate, hydrate....
        var updateResult = localPlaceHolder.UpdatePlaceholder(remotePlaceholder,
            CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE);

        if (pinned)
        {
            localPlaceHolder.SetPinState(CF_PIN_STATE.CF_PIN_STATE_PINNED);
        }

        updateResult.ThrowOnFailure();

        localPlaceHolder.HydratePlaceholder().ThrowOnFailure();
    }

    private Task ChangedDataAction(ProcessChangedDataArgs data)
    {
        return ProcessChangedDataAsync2(
            data.FullPath,
            data.LocalPlaceHolder,
            data.RemotePlaceholder,
            data.SyncMode,
            CancellationToken.None);
    }

    private async void LocalSyncTimerCallback(object state)
    {
        if (_fileSystemProvider.Status != FileProviderStatus.Ready)
        {
            return;
        }

        _localSyncTimer.Change(Timeout.Infinite, Timeout.Infinite);

        try
        {
            await SyncDataAsync(SyncMode.Local, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, message: null);
        }
        finally
        {
            _localSyncTimer.Change(LocalSyncTimerInterval, LocalSyncTimerInterval);
        }
    }

    private async void FailedQueueTimerCallback(object state)
    {
        if (_fileSystemProvider.Status != FileProviderStatus.Ready)
        {
            return;
        }

        _failedQueueTimer.Change(Timeout.Infinite, Timeout.Infinite);

        try
        {
            var items = _failedDataQueue.AsQueryable();

            foreach (var item in (from a in items where a.Value.NextTry <= DateTime.Now select a))
            {
                if (await ProcessFileChanged(item.Key, SyncMode.Full))
                {
                    _failedDataQueue.TryRemove(item.Key, out _);
                }
            }
        }
        finally
        {
            _failedQueueTimer.Change(FailedQueueTimerInterval, FailedQueueTimerInterval);
        }
    }

    public void Dispose()
    {
        if (_connectionKey != null)
        {
            CfDisconnectSyncRoot(_connectionKey.Value);
            CfUpdateSyncProviderStatus(_connectionKey.Value, CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_TERMINATED);
            _connectionKey = null;
        }

        if (_watcher != null)
        {
            RemoveFileWatcher();
        }

        _disposed = true;
    }

    public async void CbNotifyRenameAsync(
        string relativeFileName,
        string relativeFileNameDestination,
        bool isDirectory,
        CF_OPERATION_INFO opInfo)
    {
        if (_disposed)
        {
            return;
        }

        var fullPath = PathHelper.GetAbsolutePath(relativeFileName, _options.LocalPath);
        using var lockItem = _localChangedDataQueue.LockItemDisposable(fullPath);

        NTStatus status;

        if (!FileExcluder.IsExcludedFile(relativeFileName) && FileExcluder.IsExcludedFile(relativeFileNameDestination))
        {
            AddFileToLocalChangeQueue(fullPath, ignoreLock: true);
        }

        if (FileExcluder.IsExcludedFile(relativeFileName) && !FileExcluder.IsExcludedFile(relativeFileNameDestination))
        {
            AddFileToLocalChangeQueue(fullPath, ignoreLock: true);
        }

        if (!FileExcluder.IsExcludedFile(relativeFileName) && !FileExcluder.IsExcludedFile(relativeFileNameDestination))
        {
            var result = await _fileSystemProvider.Operations.MoveFileAsync(relativeFileName, relativeFileNameDestination, isDirectory);
            status = result.Succeeded
                ? NTStatus.STATUS_SUCCESS
                : NTStatus.STATUS_ACCESS_DENIED;
        }
        else
        {
            status = NTStatus.STATUS_SUCCESS;
        }

        var opParams = CF_OPERATION_PARAMETERS.Create(new CF_OPERATION_PARAMETERS.ACKRENAME
        {
            Flags = CF_OPERATION_ACK_RENAME_FLAGS.CF_OPERATION_ACK_RENAME_FLAG_NONE,
            CompletionStatus = status
        });

        CfExecute(opInfo, ref opParams);
    }

    public void Connect()
    {
        EnsureNotDisposed();

        var result = CfConnectSyncRoot(_options.LocalPath, _callbackMappings,
            CallbackContext: 0,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO |
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
            out var connectionKey);
        result.ThrowIfFailed();

        result = CfUpdateSyncProviderStatus(connectionKey, CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_IDLE);
        result.ThrowIfFailed();

        _logger.LogDebug(message: "CF Provider registered");

        InitFileWatcher();

        _connectionKey = connectionKey;
    }

    private async Task<FileOperationResult<List<FilePlaceholder>>> GetRemoteFileListAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        EnsureNotDisposed();

        var completionStatus = new FileOperationResult<List<FilePlaceholder>>
        {
            Data = new List<FilePlaceholder>()
        };

        await using var fileList = _fileSystemProvider.Operations.GetNewFileList();
        var result = await fileList.OpenAsync(relativePath, cancellationToken);
        if (!result.Succeeded)
        {
            completionStatus.Status = result.Status;
            return completionStatus;
        }

        var getNextFileResult = await fileList.GetNextAsync();
        while (getNextFileResult.Succeeded && !cancellationToken.IsCancellationRequested)
        {
            var relativeFileName = relativePath + "\\" + getNextFileResult.FilePlaceholder.RelativeFileName;

            if (!FileExcluder.IsExcludedFile(relativeFileName)
                && !getNextFileResult.FilePlaceholder.FileAttributes.HasFlag(FileAttributes.System))
            {
                completionStatus.Data.Add(getNextFileResult.FilePlaceholder);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            getNextFileResult = await fileList.GetNextAsync();
        }

        var closeResult = await fileList.CloseAsync();
        completionStatus.Status = closeResult.Status;

        cancellationToken.ThrowIfCancellationRequested();

        return completionStatus;
    }

    public Task SyncDataAsync(SyncMode syncMode)
    {
        return SyncDataAsync(syncMode, string.Empty, CancellationToken.None);
    }

    public Task SyncDataAsync(SyncMode syncMode, string relativePath)
    {
        return SyncDataAsync(syncMode, relativePath, CancellationToken.None);
    }

    public Task SyncDataAsync(SyncMode syncMode, CancellationToken ctx)
    {
        return SyncDataAsync(syncMode, string.Empty, ctx);
    }

    public async Task SyncDataAsync(SyncMode syncMode, string relativePath, CancellationToken ctx)
    {
        if (_disposed)
        {
            return;
        }

        if (SyncInProgress)
        {
            return;
        }

        switch (syncMode)
        {
            case SyncMode.Local:
                CfUpdateSyncProviderStatus(_connectionKey!.Value, CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_SYNC_INCREMENTAL);
                break;

            case SyncMode.Full:
                CfUpdateSyncProviderStatus(_connectionKey!.Value, CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_SYNC_FULL);
                break;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            _localChangedDataQueue.Reset();

            if (syncMode == SyncMode.Full)
            {
                _remoteChangedDataQueue.Reset();
            }
        }

        try
        {
            SyncInProgress = true;
            await Task.Factory.StartNew(async () =>
            {
                try
                {
                    var fullPath = PathHelper.GetAbsolutePath(relativePath, _options.LocalPath);
                    await SyncDataAsyncRecursive(fullPath, ctx, syncMode).ConfigureAwait(continueOnCapturedContext: false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, message: "Synchronization error");
                }
            }, ctx, TaskCreationOptions.LongRunning | TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Default);
        }
        finally
        {
            SyncInProgress = false;
            CfUpdateSyncProviderStatus(_connectionKey!.Value, CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_IDLE);
        }
    }

    private async Task<bool> SyncDataAsyncRecursive(string folder, CancellationToken ctx, SyncMode syncMode)
    {
        var relativeFolder = PathHelper.GetRelativePath(folder, _options.LocalPath);
        var anyFileHydrated = false;

        List<FilePlaceholder> remotePlaceholderes;

        using var localFolderPlaceholder = new ExtendedPlaceholderState(folder);
        var isExcludedFile = FileExcluder.IsExcludedFile(folder, localFolderPlaceholder.Attributes);

        // Get Filelist from Server on FullSync
        if (syncMode >= SyncMode.Full && !isExcludedFile)
        {
            var getServerFileListResult = await GetRemoteFileListAsync(relativeFolder, ctx);
            if (getServerFileListResult.Status != CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE)
            {
                getServerFileListResult.ThrowOnFailure();
            }

            remotePlaceholderes = getServerFileListResult.Data;
        }
        else
        {
            remotePlaceholderes = new List<FilePlaceholder>();
        }

        if (isExcludedFile)
        {
            localFolderPlaceholder.ConvertToPlaceholder(markInSync: true);
            localFolderPlaceholder.SetPinState(CF_PIN_STATE.CF_PIN_STATE_EXCLUDED);
            localFolderPlaceholder.SetInSyncState(CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);
        }

        var findHandle = Kernel32.FindFirstFile(@"\\?\" + folder + @"\*", out var findData);
        var fileFound = (findHandle.IsInvalid == false);
        using var localPlaceholders = new DisposableList<ExtendedPlaceholderState>();

        while (fileFound)
        {
            if (findData.cFileName != "." && findData.cFileName != "..")
            {
                var fullFilePath = Path.Combine(folder, findData.cFileName);

                var localPlaceholder = new ExtendedPlaceholderState(findData, folder);
                localPlaceholders.Add(localPlaceholder);

                var remotePlaceholder = remotePlaceholderes.FirstOrDefault(a => PathHelper.Equals(a.RelativeFileName, findData.cFileName));

                if (localPlaceholder.IsDirectory)
                {
                    if (localPlaceholder.IsPlaceholder)
                    {
                        if (!localPlaceholder.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL) ||
                            !localPlaceholder.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC) ||
                            isExcludedFile ||
                            localPlaceholder.PlaceholderInfoStandard.PinState == CF_PIN_STATE.CF_PIN_STATE_PINNED)
                        {
                            if (syncMode == SyncMode.Full)
                            {
                                await _syncActionBlock.SendAsync(new SyncDataParam
                                {
                                    CancellationToken = ctx,
                                    Folder = fullFilePath,
                                    SyncMode = syncMode
                                }, ctx);

                                anyFileHydrated = true;
                            }
                            else
                            {
                                if (await SyncDataAsyncRecursive(fullFilePath, ctx, syncMode))
                                {
                                    anyFileHydrated = true;
                                }
                            }
                        }
                        else
                        {
                            localPlaceholder.SetInSyncState(CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);

                            // Ignore Directorys which will trigger FETCH_PLACEHOLDER
                        }
                    }
                    else
                    {
                        try
                        {
                            if (syncMode == SyncMode.Full)
                            {
                                await ProcessChangedDataAsync(
                                    fullFilePath,
                                    localPlaceholder,
                                    (DynamicServerPlaceholder)remotePlaceholder,
                                    syncMode,
                                    ctx);
                            }
                            else
                            {
                                var placeholder = new DynamicServerPlaceholder(PathHelper.GetRelativePath(
                                        fullFilePath,
                                        _options.LocalPath),
                                    localPlaceholder.IsDirectory, _fileSystemProvider);

                                await ProcessChangedDataAsync(
                                    fullFilePath,
                                    localPlaceholder,
                                    placeholder,
                                    syncMode,
                                    ctx);
                            }
                        }
                        catch (Exception)
                        {
                            AddFileToLocalChangeQueue(fullFilePath, ignoreLock: true);
                        }

                        if (syncMode == SyncMode.Full)
                        {
                            await _syncActionBlock.SendAsync(new SyncDataParam
                            {
                                CancellationToken = ctx,
                                Folder = fullFilePath,
                                SyncMode = syncMode
                            }, ctx);
                            anyFileHydrated = true;
                        }
                        else
                        {
                            if (await SyncDataAsyncRecursive(fullFilePath, ctx, syncMode))
                            {
                                anyFileHydrated = true;
                            }
                        }
                    }
                }
                else
                {
                    DynamicServerPlaceholder dynPlaceholder;
                    if (syncMode == SyncMode.Full)
                    {
                        dynPlaceholder = (DynamicServerPlaceholder)remotePlaceholder;
                    }
                    else
                    {
                        var relativePath = PathHelper.GetRelativePath(fullFilePath, _options.LocalPath);

                        dynPlaceholder = new DynamicServerPlaceholder(
                            relativePath,
                            localPlaceholder.IsDirectory,
                            _fileSystemProvider);
                    }

                    await ProcessChangedDataAsync(
                        fullFilePath,
                        localPlaceholder,
                        dynPlaceholder,
                        syncMode,
                        ctx);
                }

                if (!localPlaceholder.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC)
                    || !localPlaceholder.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL)
                    || localPlaceholder.PlaceholderInfoStandard.OnDiskDataSize > 0)
                {
                    anyFileHydrated = true;
                }
            }

            ctx.ThrowIfCancellationRequested();
            fileFound = Kernel32.FindNextFile(findHandle, out findData);
        }

        foreach (ExtendedPlaceholderState lpl in localPlaceholders)
        {
            if (!lpl.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC)
                || !lpl.PlaceholderState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PARTIAL)
                || lpl.PlaceholderInfoStandard.OnDiskDataSize > 0)
            {
                anyFileHydrated = true;
                break;
            }
        }

        // Add missing local Placeholders
        foreach (var remotePlaceholder in remotePlaceholderes)
        {
            var fullFilePath = Path.Combine(folder, remotePlaceholder.RelativeFileName);

            if (!localPlaceholders.Any(a => PathHelper.Equals(a.FullPath, fullFilePath)))
            {
                var info = new CF_PLACEHOLDER_CREATE_INFO[1];
                info[0] = CreatePlaceholderInfo(remotePlaceholder, Guid.NewGuid().ToString());
                CfCreatePlaceholders(folder, info, PlaceholderCount: 1, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out _);
            }
        }

        if (syncMode == SyncMode.Full)
        {
            localFolderPlaceholder.SetInSyncState(CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);

            if (!anyFileHydrated && !isExcludedFile
                                 && localFolderPlaceholder.PlaceholderInfoStandard.PinState != CF_PIN_STATE.CF_PIN_STATE_PINNED)
            {
                localFolderPlaceholder.EnableOnDemandPopulation();
            }
            else
            {
                localFolderPlaceholder.DisableOnDemandPopulation();
            }
        }

        //else
        //{
        //    if (!anyFileHydrated && !isExcludedFile && localFolderPlaceholder.PlaceholderInfoStandard.PinState != CF_PIN_STATE.CF_PIN_STATE_PINNED)
        //    {
        //        localFolderPlaceholder.EnableOnDemandPopulation();
        //    }
        //}

        return anyFileHydrated;
    }

    private async Task ProcessChangedDataAsync(string fullPath, SyncMode syncMode, CancellationToken ctx)
    {
        // ignore deleted Files
        if (!Path.Exists(fullPath))
        {
            return;
        }

        using var localPlaceHolder = new ExtendedPlaceholderState(fullPath);
        await ProcessChangedDataAsync(fullPath, localPlaceHolder, syncMode, ctx).ConfigureAwait(continueOnCapturedContext: false);
    }

    private async Task ProcessChangedDataAsync(
        string fullPath,
        ExtendedPlaceholderState localPlaceHolder,
        SyncMode syncMode,
        CancellationToken ctx)
    {
        var relativePath = PathHelper.GetRelativePath(fullPath, _options.LocalPathNormalized);
        var placeHolder = new DynamicServerPlaceholder(relativePath, localPlaceHolder.IsDirectory, _fileSystemProvider);
        await ProcessChangedDataAsync(fullPath, localPlaceHolder, placeHolder, syncMode, ctx);
    }

    private Task ProcessChangedDataAsync(
        string fullPath,
        ExtendedPlaceholderState localPlaceHolder,
        DynamicServerPlaceholder remotePlaceholder,
        SyncMode syncMode,
        CancellationToken ctx)
    {
        return _changedDataQueue.SendAsync(new ProcessChangedDataArgs
        {
            SyncMode = syncMode,
            FullPath = fullPath,
            LocalPlaceHolder = localPlaceHolder,
            RemotePlaceholder = remotePlaceholder
        }, ctx);
    }

    private void AddFileToLocalChangeQueue(string fullPath, bool ignoreLock)
    {
        EnsureNotDisposed();

        if (FileExcluder.IsExcludedFile(fullPath))
        {
            return;
        }

        _localChangedDataQueue.TryAdd(fullPath, ignoreLock);
    }

    private void CbCancelFetchData(in CF_CALLBACK_INFO callbackinfo, in CF_CALLBACK_PARAMETERS callbackparameters)
    {
        _fileRangeManager.Cancel(new DataActions
        {
            FileOffset = callbackparameters.Cancel.FetchData.FileOffset,
            Length = callbackparameters.Cancel.FetchData.Length,
            NormalizedPath = callbackinfo.NormalizedPath,
            TransferKey = callbackinfo.TransferKey,
            Id =
                $"{callbackinfo.NormalizedPath}"
                + $"!{callbackparameters.Cancel.FetchData.FileOffset}"
                + $"!{callbackparameters.Cancel.FetchData.Length}"
        });
    }

    private void CbCancelFetchPlaceHolders(in CF_CALLBACK_INFO callbackinfo, in CF_CALLBACK_PARAMETERS callbackparameters)
    {
        _logger.LogDebug(message: "CancelFetchPlaceHolders");
    }

    private void CbFetchData(in CF_CALLBACK_INFO callbackinfo, in CF_CALLBACK_PARAMETERS callbackparameters)
    {
        var cancelFetch = _fileSystemProvider.Status != FileProviderStatus.Ready;
        if (cancelFetch)
        {
            var opInfo = CreateOperationInfo(callbackinfo, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA);
            var paramValue = new CF_OPERATION_PARAMETERS.TRANSFERDATA
            {
                Length = callbackparameters.FetchData.RequiredLength,
                Offset = callbackparameters.FetchData.RequiredFileOffset,
                Buffer = 0,
                Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                CompletionStatus = new NTStatus((uint)CloudFilterNTStatus.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE)
            };

            var opParams = CF_OPERATION_PARAMETERS.Create(paramValue);
            CfExecute(opInfo, ref opParams);
            return;
        }

        var length = callbackparameters.FetchData.RequiredLength;
        var offset = callbackparameters.FetchData.RequiredFileOffset;

        if ((offset + length) == callbackparameters.FetchData.OptionalFileOffset)
        {
            if (length < ChunkSize)
            {
                length = Math.Min(ChunkSize, callbackparameters.FetchData.OptionalLength + length);
            }
        }

        var data = new DataActions
        {
            FileOffset = offset,
            Length = length,
            NormalizedPath = callbackinfo.NormalizedPath,
            PriorityHint = callbackinfo.PriorityHint,
            TransferKey = callbackinfo.TransferKey,
            Id =
                $"{callbackinfo.NormalizedPath}"
                + $"!{callbackparameters.FetchData.RequiredFileOffset}"
                + $"!{callbackparameters.FetchData.RequiredLength}"
        };

        _fileRangeManager.Add(data);
    }

    private void CbFetchPlaceHolders(in CF_CALLBACK_INFO callbackinfo, in CF_CALLBACK_PARAMETERS callbackparameters)
    {
        if (_disposed)
        {
            return;
        }

        var opInfo = CreateOperationInfo(callbackinfo, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS);

        var cancelFetch = _fileSystemProvider.Status != FileProviderStatus.Ready;
        if (!cancelFetch)
        {
            var processInfo = Marshal.PtrToStructure<CF_PROCESS_INFO>(callbackinfo.ProcessInfo);
            var excludedProcessesForFetchPlaceholders = new[]
            {
                // This process tries to index folders which are just a few seconds before marked as
                // "ENABLE_ON_DEMAND_POPULATION" which results in unwanted re-population.
                @".*\\SearchProtocolHost\.exe.*",

                // This process cleans old data. Fetching of placeholders is not required for this process
                @".*\\svchost\.exe.*StorSvc"
            };

            if (excludedProcessesForFetchPlaceholders
                .Any(process => Regex.IsMatch(processInfo.CommandLine, process)))
            {
                cancelFetch = true;
            }
        }

        if (cancelFetch)
        {
            var param = new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
            {
                PlaceholderArray = 0,
                Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE,
                PlaceholderCount = 0,
                PlaceholderTotalCount = 0,
                CompletionStatus = new NTStatus((uint)CloudFilterNTStatus.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE)
            };

            var opParams = CF_OPERATION_PARAMETERS.Create(param);
            CfExecute(opInfo, ref opParams);
            return;
        }

        var relativePath = PathHelper.GetRelativePath(callbackinfo, _options.LocalPath);
        var cts = new CancellationTokenSource();

        _fetchPlaceholdersCancellationTokens.AddOrUpdate(relativePath, cts, (_, oldCts) =>
        {
            oldCts?.Cancel();
            return cts;
        });

        CbFetchPlaceHoldersAsync(relativePath, opInfo, cts.Token);
    }

    private async void CbFetchPlaceHoldersAsync(
        string relativePath,
        CF_OPERATION_INFO opInfo,
        CancellationToken cancellationToken)
    {
        EnsureNotDisposed();

        var fullPath = PathHelper.GetAbsolutePath(relativePath, _options.LocalPath);

        using var infos = new SafePlaceholderList();

        var completionStatus = CloudFilterNTStatus.STATUS_SUCCESS;
        var getServerFileListResult = await GetRemoteFileListAsync(relativePath, cancellationToken);

        if (!getServerFileListResult.Succeeded)
        {
            completionStatus = getServerFileListResult.Status;
        }
        else
        {
            // Create CreatePlaceholderInfo for each Cloud File
            foreach (var placeholder in getServerFileListResult.Data)
            {
                infos.Add(CreatePlaceholderInfo(placeholder, Guid.NewGuid().ToString()));
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        // directories which do not exist on Server should not throw any exception.
        if (completionStatus == CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE)
        {
            completionStatus = NTStatus.STATUS_SUCCESS;
        }

        using var lockItem = _localChangedDataQueue.LockItemDisposable(fullPath);

        var total = (uint)infos.Count;
        var param = new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
        {
            PlaceholderArray = infos,
            Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION,
            PlaceholderCount = total,
            PlaceholderTotalCount = total,
            CompletionStatus = new NTStatus((uint)completionStatus)
        };

        var opParams = CF_OPERATION_PARAMETERS.Create(param);
        var executeResult = CfExecute(opInfo, ref opParams);

        _fetchPlaceholdersCancellationTokens.TryRemove(relativePath, out var _);

        if (completionStatus != NTStatus.STATUS_SUCCESS || !executeResult.Succeeded)
        {
            return;
        }

        // Validate local placeholders. CfExecute only adds missing entries, but does not check existing data.
        var localPlaceholders = GetLocalPathFileList(relativePath, cancellationToken);
        foreach (var remotePlaceholder in getServerFileListResult.Data)
        {
            var localPlaceholder = localPlaceholders
                .FirstOrDefault(a => PathHelper.Equals(a.RelativeFileName, remotePlaceholder.RelativeFileName));

            if (remotePlaceholder.FileAttributes.HasFlag(FileAttributes.Directory) == false
                && remotePlaceholder.ETag != localPlaceholder?.ETag)
            {
                var path = PathHelper.GetAbsolutePath(remotePlaceholder.RelativeFileName, _options.LocalPath);
                AddFileToLocalChangeQueue(path, ignoreLock: true);
            }
        }

        foreach (var item in localPlaceholders)
        {
            if (getServerFileListResult.Data.Any(a => PathHelper.Equals(a.RelativeFileName, item.RelativeFileName)))
            {
                continue;
            }

            var path = PathHelper.GetAbsolutePath(item.RelativeFileName, _options.LocalPath);
            AddFileToLocalChangeQueue(path, ignoreLock: true);
        }
    }

    private void CbNotifyDelete(in CF_CALLBACK_INFO callbackinfo, in CF_CALLBACK_PARAMETERS callbackparameters)
    {
        if (_disposed)
        {
            return;
        }

        _deleteQueue.Post(new DeleteAction
        {
            OperationInfo = CreateOperationInfo(callbackinfo, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE),
            IsDirectory = callbackparameters.Delete.Flags.HasFlag(CF_CALLBACK_DELETE_FLAGS.CF_CALLBACK_DELETE_FLAG_IS_DIRECTORY),
            RelativePath = PathHelper.GetRelativePath(callbackinfo, _options.LocalPath)
        });
    }

    private void CbNotifyDeleteCompletion(in CF_CALLBACK_INFO callbackinfo, in CF_CALLBACK_PARAMETERS callbackparameters)
    {
        // do nothing
    }

    private void CbNotifyFileCloseCompletion(in CF_CALLBACK_INFO callbackinfo, in CF_CALLBACK_PARAMETERS callbackparameters)
    {
        var path = PathHelper.GetAbsolutePath(callbackinfo, _options.LocalPath);
        _localChangedDataQueue.UnlockItem(path);

        AddFileToLocalChangeQueue(path, ignoreLock: false);
    }

    private void CbNotifyFileOpenCompletion(in CF_CALLBACK_INFO callbackinfo, in CF_CALLBACK_PARAMETERS callbackparameters)
    {
        var path = PathHelper.GetAbsolutePath(callbackinfo, _options.LocalPath);
        _localChangedDataQueue.LockItem(path);
    }

    private void CbNotifyRename(in CF_CALLBACK_INFO callbackinfo, in CF_CALLBACK_PARAMETERS callbackparameters)
    {
        if (_disposed)
        {
            return;
        }

        var opInfo = CreateOperationInfo(callbackinfo, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME);
        var relativePath = PathHelper.GetRelativePath(callbackinfo, _options.LocalPath);
        var renameRelativePath = PathHelper.GetRelativePath(callbackparameters.Rename, _options.LocalPath);
        var isDirectory = callbackparameters.Rename.Flags.HasFlag(CF_CALLBACK_RENAME_FLAGS.CF_CALLBACK_RENAME_FLAG_IS_DIRECTORY);

        CbNotifyRenameAsync(relativePath, renameRelativePath, isDirectory, opInfo);
    }

    private void CbNotifyRenameCompletion(in CF_CALLBACK_INFO callbackinfo, in CF_CALLBACK_PARAMETERS callbackparameters)
    {
        // do nothing
    }

    private Kernel32.FILE_BASIC_INFO CreateFileBasicInfo(FilePlaceholder placeholder)
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

    private CF_OPERATION_INFO CreateOperationInfo(in CF_CALLBACK_INFO callbackinfo, CF_OPERATION_TYPE operasionType)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            Type = operasionType,
            ConnectionKey = callbackinfo.ConnectionKey,
            TransferKey = callbackinfo.TransferKey,
            CorrelationVector = callbackinfo.CorrelationVector,
            RequestKey = callbackinfo.RequestKey
        };

        opInfo.StructSize = (uint)Marshal.SizeOf(opInfo);
        return opInfo;
    }

    private CF_PLACEHOLDER_CREATE_INFO CreatePlaceholderInfo(FilePlaceholder placeholder, string fileIdentity)
    {
        var cfInfo = new CF_PLACEHOLDER_CREATE_INFO
        {
            FileIdentity = Marshal.StringToCoTaskMemUni(fileIdentity),
            FileIdentityLength = (uint)(fileIdentity.Length * Marshal.SizeOf(fileIdentity[index: 0])),

            RelativeFileName = placeholder.RelativeFileName,
            FsMetadata = new CF_FS_METADATA
            {
                FileSize = placeholder.FileSize,
                BasicInfo = CreateFileBasicInfo(placeholder)
            },
            Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
        };

        return cfInfo;
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) { }
    }

    private void FileWatcherOnChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var file = new FileInfo(e.FullPath);
        if (FileExcluder.IsExcludedFile(file))
        {
            return;
        }

        AddFileToLocalChangeQueue(e.FullPath, ignoreLock: false);
    }

    private IReadOnlyCollection<FilePlaceholder> GetLocalPathFileList(string relativePath, CancellationToken cancellationToken)
    {
        var path = _options.LocalPath;
        if (!string.IsNullOrEmpty(relativePath))
        {
            path = Path.Combine(path, relativePath);
        }

        var localPlaceholders = new List<FilePlaceholder>();
        var directory = new DirectoryInfo(path);

        foreach (var fileSystemInfo in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            localPlaceholders.Add(new FilePlaceholder(fileSystemInfo));
        }

        return localPlaceholders;
    }

    private void InitFileWatcher()
    {
        if (_watcher != null)
        {
            RemoveFileWatcher();
        }

        _changedDataCancellationTokenSource = new CancellationTokenSource();
        _changedDataCancellationTokenSource.Token.Register(() => _localChangedDataQueue.Complete());
        _changedDataCancellationTokenSource.Token.Register(() => _remoteChangedDataQueue.Complete());

        _ = CreateLocalChangedDataQueueTask();
        _ = CreateRemoteChangedDataQueueTask();

        _watcher = new FileSystemWatcher
        {
            Path = _options.LocalPath,
            IncludeSubdirectories = true,
            NotifyFilter =
                NotifyFilters.DirectoryName |
                NotifyFilters.FileName |
                NotifyFilters.Attributes |
                NotifyFilters.LastWrite |
                NotifyFilters.Size,
            Filter = "*"
        };

        _watcher.Error += (_, args) => _logger.LogError(args.GetException(), message: "File watcher exception");
        _watcher.Created += FileWatcherOnChanged;
        _watcher.Changed += FileWatcherOnChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private Task CreateLocalChangedDataQueueTask()
    {
        return Task.Factory.StartNew(async () =>
                {
                    while (!_localChangedDataQueue.CancellationToken.IsCancellationRequested)
                    {
                        var item = await _localChangedDataQueue.WaitTakeNextAsync().ConfigureAwait(continueOnCapturedContext: false);

                        if (_localChangedDataQueue.CancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (item != null)
                        {
                            var itemLock = _localChangedDataQueue.LockItemDisposable(item);
                            try
                            {
                                await ProcessFileChanged(item, SyncMode.Local);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, message: null);
                            }
                            finally
                            {
                                // Delay before releasing itemLock
                                _ = Task.Delay(millisecondsDelay: 20).ContinueWith(_ => itemLock.Dispose());
                            }
                        }
                    }
                }, _localChangedDataQueue.CancellationToken, TaskCreationOptions.LongRunning |
                                                             TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Default)
            .Unwrap();
    }

    private Task CreateRemoteChangedDataQueueTask()
    {
        return Task.Factory.StartNew(async () =>
                {
                    while (!_remoteChangedDataQueue.CancellationToken.IsCancellationRequested)
                    {
                        var item = await _remoteChangedDataQueue.WaitTakeNextAsync().ConfigureAwait(continueOnCapturedContext: false);

                        if (_remoteChangedDataQueue.CancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (item != null)
                        {
                            var itemLock = _remoteChangedDataQueue.LockItemDisposable(item);
                            try
                            {
                                await ProcessFileChanged(item, SyncMode.Full);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, message: null);
                            }
                            finally
                            {
                                // Delay before releasing itemLock
                                _ = Task.Delay(millisecondsDelay: 20).ContinueWith(_ => itemLock.Dispose());
                            }
                        }
                    }
                }, _remoteChangedDataQueue.CancellationToken, TaskCreationOptions.LongRunning |
                                                              TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Default)
            .Unwrap();
    }

    private async Task<bool> ProcessFileChanged(string path, SyncMode syncMode)
    {
        try
        {
            await ProcessChangedDataAsync(path, syncMode, _changedDataCancellationTokenSource.Token)
                .ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, message: null);

            var failedData = new FailedDataSynchronization
            {
                LastException = ex,
                LastTry = DateTime.Now,
                NextTry = DateTime.Now.AddSeconds(value: 20),
                RetryCount = 0,
                SyncMode = syncMode
            };

            _failedDataQueue.AddOrUpdate(path, failedData, (_, current) =>
            {
                current.LastTry = failedData.LastTry;
                current.NextTry = failedData.NextTry;
                current.RetryCount += 1;
                current.SyncMode = current.SyncMode > failedData.SyncMode
                    ? current.SyncMode
                    : failedData.SyncMode;
                return current;
            });

            return false;
        }
    }

    private async Task NotifyDeleteAction(DeleteAction dat)
    {
        if (_disposed)
        {
            return;
        }

        NTStatus status;

        if (_fileSystemProvider.Status != FileProviderStatus.Ready)
        {
            var opParams1 = CF_OPERATION_PARAMETERS.Create(new CF_OPERATION_PARAMETERS.ACKDELETE
            {
                Flags = CF_OPERATION_ACK_DELETE_FLAGS.CF_OPERATION_ACK_DELETE_FLAG_NONE,
                CompletionStatus = new NTStatus((uint)CloudFilterNTStatus.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE)
            });

            CfExecute(dat.OperationInfo, ref opParams1);
            return;
        }

        var fullPath = PathHelper.GetAbsolutePath(dat.RelativePath, _options.LocalPath);
        using var lockFile = _localChangedDataQueue.LockItemDisposable(fullPath);

        if (FileExcluder.IsExcludedFile(dat.RelativePath))
        {
            status = NTStatus.STATUS_SUCCESS;
            goto skip;
        }

        if (!(dat.IsDirectory
                ? Directory.Exists(fullPath)
                : File.Exists(fullPath)))
        {
            status = NTStatus.STATUS_SUCCESS;
            goto skip;
        }

        var pl = new ExtendedPlaceholderState(fullPath);
        if (pl.PlaceholderInfoStandard.PinState == CF_PIN_STATE.CF_PIN_STATE_EXCLUDED)
        {
            status = NTStatus.STATUS_SUCCESS;
            goto skip;
        }

        var result = await _fileSystemProvider.Operations.DeleteFileAsync(dat.RelativePath, dat.IsDirectory);
        status = new NTStatus((uint)result.Status);

        skip:
        var opParams = CF_OPERATION_PARAMETERS.Create(new CF_OPERATION_PARAMETERS.ACKDELETE
        {
            Flags = CF_OPERATION_ACK_DELETE_FLAGS.CF_OPERATION_ACK_DELETE_FLAG_NONE,
            CompletionStatus = status
        });

        CfExecute(dat.OperationInfo, ref opParams);
    }


    private void RemoveFileWatcher()
    {
        if (_changedDataCancellationTokenSource != null)
        {
            _changedDataCancellationTokenSource.Cancel();
        }

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}