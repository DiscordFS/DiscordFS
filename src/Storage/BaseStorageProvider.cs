using System.Runtime.InteropServices;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using DiscordFS.Storage.Discord;
using DiscordFS.Storage.FileSystem;
using DiscordFS.Storage.Synchronization;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using FileAttributes = System.IO.FileAttributes;

namespace DiscordFS.Storage;

public static class StorageProviderDictionary
{
    private static readonly Dictionary<string, IStorageProvider> _storageProviders = new();

    public static void Register(string syncRootId, IStorageProvider storageProvider)
    {
        if (_storageProviders.ContainsKey(syncRootId))
        {
            _storageProviders.Remove(syncRootId);
        }

        _storageProviders.Add(syncRootId, storageProvider);
    }

    public static void Unregister(string syncRootId)
    {
        _storageProviders.Remove(syncRootId);
    }

    public static IStorageProvider GetForSyncRoot(string syncRootId)
    {
        if (!_storageProviders.ContainsKey(syncRootId))
        {
            return null;
        }

        return _storageProviders[syncRootId];
    }
}

public abstract class BaseStorageProvider<TOptions> : IStorageProvider<TOptions> where TOptions : StorageProviderOptions
{
    protected TOptions Options { get; private set; }

    private readonly ILogger _logger;
    private bool _disposed;

    private string _instanceId;
    private string _instanceName;
    private IRemoteFileSystemProvider _remoteFileSystemProvider;

    protected BaseStorageProvider(ILogger logger)
    {
        _logger = logger;
    }

    public virtual async Task RegisterAsync(TOptions options)
    {
        EnsureNotDisposed();

        if (Options != null)
        {
            throw new Exception(message: "Provider is already registered");
        }

        if (!StorageProviderSyncRootManager.IsSupported())
        {
            throw new NotSupportedException();
        }

        Options = options;

        _instanceId = await GetInstanceIdAsync(options);
        _instanceName = await GetInstanceNameAsync(options);

        if (!Directory.Exists(options.LocalPath))
        {
            Directory.CreateDirectory(options.LocalPath);
        }

        var directoryInfo = new DirectoryInfo(options.LocalPath);
        directoryInfo.Attributes |=
            FileAttributes.Directory
            | FileAttributes.Archive
            | FileAttributes.ReadOnly
            | FileAttributes.ReparsePoint;

        var folder = await StorageFolder.GetFolderFromPathAsync(options.LocalPath);
        var exeLocation = GetType().Assembly.Location.Replace(oldValue: ".dll", newValue: ".exe");
        var syncRootId = options.CalculateSyncRootId(_instanceId);

        var syncRootInfo = new StorageProviderSyncRootInfo
        {
            Id = syncRootId,
            ProviderId = options.ProviderId,
            Version = options.ProviderVersion,
            DisplayNameResource = $"DiscordFS - {_instanceName}",
            AllowPinning = true,
            Path = folder,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            HydrationPolicy = StorageProviderHydrationPolicy.Partial,

            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed |
                                      StorageProviderHydrationPolicyModifier.StreamingAllowed,

            InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime,
            PopulationPolicy = StorageProviderPopulationPolicy.Full,
            ProtectionMode = StorageProviderProtectionMode.Unknown,
            IconResource = $"\"{exeLocation}\",0",
            ShowSiblingsAsGroup = false,
            RecycleBinUri = null,
            Context = CryptographicBuffer.ConvertStringToBinary(syncRootId, BinaryStringEncoding.Utf8),
            StorageProviderItemPropertyDefinitions =
            {
                new StorageProviderItemPropertyDefinition
                {
                    DisplayNameResource = "Description",
                    Id = 0
                }
            }
        };

        StorageProviderDictionary.Register(syncRootId, this);
        StorageProviderSyncRootManager.Register(syncRootInfo);
        BeginSynchronization();
    }

    public Task RegisterAsync(StorageProviderOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options is not TOptions storageProviderOptions)
        {
            throw new ArgumentException($"Invalid options type; expected {typeof(TOptions).Name}, got: {options.GetType().Name}");
        }

        return RegisterAsync(storageProviderOptions);
    }

    public virtual void Unregister()
    {
        EnsureNotDisposed();

        if (Options == null)
        {
            return;
        }

        EndSychronization(disconnect: true);

        var syncRootId = Options.CalculateSyncRootId(_instanceId);

        try
        {
            StorageProviderSyncRootManager.Unregister(syncRootId);
        }
        catch (COMException)
        {
            // ignored
        }

        StorageProviderDictionary.Unregister(syncRootId);
        CldApi.CfUnregisterSyncRoot(Options.LocalPath);
        Options = null;
    }

    public WindowsSynchronizationHandler WindowsSynchronizationHandler { get; private set; }

    public virtual void BeginSynchronization()
    {
        EnsureNotDisposed();

        if (_remoteFileSystemProvider == null)
        {
            _remoteFileSystemProvider = CreateRemoteFileProvider();
            _remoteFileSystemProvider.Connect();
        }

        if (WindowsSynchronizationHandler == null)
        {
            WindowsSynchronizationHandler = new WindowsSynchronizationHandler(_remoteFileSystemProvider, Options, _logger);
            WindowsSynchronizationHandler.Connect();
        }
    }

    public virtual void EndSychronization(bool disconnect)
    {
        EnsureNotDisposed();

        if (WindowsSynchronizationHandler != null && disconnect)
        {
            WindowsSynchronizationHandler.Dispose();
            WindowsSynchronizationHandler = null;
        }

        if (_remoteFileSystemProvider != null)
        {
            _remoteFileSystemProvider.Dispose();
            _remoteFileSystemProvider = null;
        }
    }

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Unregister();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, message: "Exception occurred during dispose");
        }

        _disposed = true;
        WindowsSynchronizationHandler?.Dispose();
        _remoteFileSystemProvider?.Dispose();
    }

    protected abstract IRemoteFileSystemProvider CreateRemoteFileProvider();

    protected abstract Task<string> GetInstanceIdAsync(TOptions options);

    protected abstract Task<string> GetInstanceNameAsync(TOptions options);

    internal void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DiscordStorageProvider));
        }
    }
}