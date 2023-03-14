using System.Runtime.InteropServices;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using DiscordFS.Storage.Discord;
using DiscordFS.Storage.Files;
using Microsoft.Extensions.Logging;
using static Vanara.PInvoke.CldApi;

namespace DiscordFS.Storage;

public interface IStorageProvider : IDisposable
{
    public void BeginSynchronization();

    public void EndSychronization(bool disconnect);

    Task RegisterAsync(StorageProviderOptions options);

    void Unregister();
}

public interface IStorageProvider<in TOptions> : IStorageProvider where TOptions : StorageProviderOptions
{
    Task RegisterAsync(TOptions options);
}

public abstract class BaseStorageProvider<TOptions> : IStorageProvider<TOptions> where TOptions : StorageProviderOptions
{
    protected TOptions Options { get; private set; }

    private readonly ILogger _logger;
    private bool _disposed;

    private string _instanceId;
    private string _instanceName;
    private IRemoteFileProvider _remoteFileProvider;
    private SynchronizationHandler _synchronizationHandler;

    protected FileRangeManager FileRangeManager { get; private set; }

    protected BaseStorageProvider(ILogger logger)
    {
        _logger = logger;
        FileRangeManager = new FileRangeManager();
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

        var folder = await StorageFolder.GetFolderFromPathAsync(options.LocalPath);
        var exeLocation = GetType().Assembly.Location.Replace(oldValue: ".dll", newValue: ".exe");
        var syncRootId = options.CalculateSyncRootId(_instanceId);

        var syncRootInfo = new StorageProviderSyncRootInfo
        {
            Id = syncRootId,
            ProviderId = options.ProviderId,
            Version = options.ProviderVersion,
            DisplayNameResource = $"Discord.FS - {_instanceName}",
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

        StorageProviderSyncRootManager.Register(syncRootInfo);
        BeginSynchronization();
    }

    public Task RegisterAsync(StorageProviderOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var castOptions = options as TOptions;
        if (castOptions == null)
        {
            throw new ArgumentException($"Invalid options type; expected {typeof(TOptions).Name}, got: {options.GetType().Name}");
        }

        return RegisterAsync(castOptions);
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

        CfUnregisterSyncRoot(Options.LocalPath);
        Options = null;
    }

    public virtual void BeginSynchronization()
    {
        EnsureNotDisposed();

        if (_remoteFileProvider == null)
        {
            _remoteFileProvider = CreateRemoteFileProvider();
            _remoteFileProvider.Connect();
        }

        if (_synchronizationHandler == null)
        {
            _synchronizationHandler = new SynchronizationHandler(FileRangeManager, _remoteFileProvider, Options, _logger);
            _synchronizationHandler.Connect();
        }
    }

    public virtual void EndSychronization(bool disconnect)
    {
        EnsureNotDisposed();

        if (_synchronizationHandler != null && disconnect)
        {
            _synchronizationHandler.Dispose();
            _synchronizationHandler = null;
        }

        if (_remoteFileProvider != null)
        {
            _remoteFileProvider.Dispose();
            _remoteFileProvider = null;
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
    }

    protected abstract IRemoteFileProvider CreateRemoteFileProvider();

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