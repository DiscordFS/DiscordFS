using Discord;
using Discord.Rest;
using Discord.WebSocket;
using DiscordFS.Helpers;
using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.FileSystem;
using DiscordFS.Storage.FileSystem.Operations;
using Microsoft.Extensions.Logging;
using Index = DiscordFS.Storage.Synchronization.Index;

namespace DiscordFS.Storage.Discord;

public class DiscordRemoteFileSystemProvider : IRemoteFileSystemProvider
{
    private const string IndexFileName = "index.db";

    public Index LastKnownRemoteIndex { get; protected set; }

    public event AsyncEventHandler<FileChangedEventArgs> FileChange;

    public event AsyncEventHandler<FileProviderStateChangedEventArgs> StateChange;

    public DiscordStorageProviderOptions Options { get; }

    public FileSystemProviderStatus Status
    {
        get
        {
            return _isReady
                   && _indexMessageId > 0
                   && _discordClient.ConnectionState == ConnectionState.Connected
                ? FileSystemProviderStatus.Ready
                : FileSystemProviderStatus.NotReady;
        }
    }

    private readonly DiscordSocketClient _discordClient;
    private readonly Timer _fullResyncTimer;
    private readonly ILogger _logger;

    public IHttpClientFactory HttpClientFactory { get; private set; }

    private CancellationTokenSource _cancellationTokenSource;

    public ITextChannel DataChannel { get; private set; }

    public ITextChannel DbChannel { get; private set; }

    private bool _disposed;
    private SocketGuild _guild;
    private ulong _indexMessageId;
    private bool _isReady;

    private readonly List<DateTimeOffset> _pendingEdits;

    public DiscordRemoteFileSystemProvider(
        ILogger logger,
        DiscordStorageProviderOptions options,
        DiscordSocketClient discordClient,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _discordClient = discordClient;
        _pendingEdits = new List<DateTimeOffset>();

        Options = options;
        HttpClientFactory = httpClientFactory;
        Operations = new DiscordRemoteFileOperations(this);
        _fullResyncTimer = new Timer(FullResyncTimerCallback, state: null, Timeout.Infinite, Timeout.Infinite);
    }

    private void FullResyncTimerCallback(object state)
    {
        _ = Task.Factory.StartNew(PerformFullSynchronizationAsync);
    }

    private Task PerformFullSynchronizationAsync()
    {
        return FileChange.InvokeAsync(this, new FileChangedEventArgs
        {
            ChangeType = FileChangeEventType.All,
            ResyncSubDirectories = true
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_cancellationTokenSource != null)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource = null;
        }

        _discordClient.MessageUpdated -= OnMessageUpdated;
        _discordClient.Connected -= OnDiscordConnected;
        _discordClient.Disconnected -= OnDiscordDisconnected;
        _disposed = true;
    }

    public IRemoteFileOperations Operations { get; }

    public void Connect()
    {
        EnsureNotDisposed();

        if (_cancellationTokenSource != null)
        {
            return;
        }

        _cancellationTokenSource = new CancellationTokenSource();

        _discordClient.MessageUpdated += OnMessageUpdated;
        _discordClient.Connected += OnDiscordConnected;
        _discordClient.Disconnected += OnDiscordDisconnected;

        _ = Task.Run(ConnectAsync);
    }

    private async Task ConnectAsync()
    {
        EnsureNotDisposed();

        _guild = _discordClient.GetGuild(ulong.Parse(Options.GuildId));
        DbChannel = await GetOrCreateChannelAsync(Options.DbChannelName);
        DataChannel = await GetOrCreateChannelAsync(Options.DataChannelName);

        await FindIndexMessageAsync();

        _ = Task.Factory.StartNew(PerformInitialSynchronizationAsync);
    }

    private async Task FindIndexMessageAsync()
    {
        foreach (var message in await DbChannel.GetPinnedMessagesAsync(
                     DiscordHelper.CreateDefaultOptions(_cancellationTokenSource.Token)))
        {
            if (!IsIndexDbMessage(message))
            {
                continue;
            }

            _indexMessageId = message.Id;
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DiscordRemoteFileSystemProvider));
        }
    }

    private async Task<ITextChannel> GetOrCreateChannelAsync(string channelName)
    {
        EnsureNotDisposed();

        channelName = channelName.Replace(oldValue: "#", string.Empty);

        var channel =
            (SocketTextChannel)_guild.Channels
                .FirstOrDefault(x => x.Name.Replace(oldValue: "#", string.Empty)
                                         .Equals(channelName, StringComparison.OrdinalIgnoreCase)
                                     && x.GetChannelType() == ChannelType.Text)
            ?? (ITextChannel)await _guild.CreateTextChannelAsync(channelName);

        var everyoneRole = _guild.EveryoneRole;
        var everyonePermissions = new OverwritePermissions(
            manageMessages: PermValue.Deny,
            viewChannel: PermValue.Allow,
            sendMessages: PermValue.Deny,
            attachFiles: PermValue.Deny,
            readMessageHistory: PermValue.Deny,
            addReactions: PermValue.Allow);

        var self = _guild.CurrentUser;
        var selfPermissions = new OverwritePermissions(
            manageMessages: PermValue.Allow,
            viewChannel: PermValue.Allow,
            sendMessages: PermValue.Allow,
            attachFiles: PermValue.Allow,
            readMessageHistory: PermValue.Allow,
            addReactions: PermValue.Allow);

        await channel.AddPermissionOverwriteAsync(self, selfPermissions);
        await channel.AddPermissionOverwriteAsync(everyoneRole, everyonePermissions);

        return channel;
    }

    private bool IsIndexDbMessage(IMessage message)
    {
        if (_indexMessageId != 0)
        {
            return message.Id == _indexMessageId;
        }

        return message.Author.Id == _guild.CurrentUser.Id &&
               message.Attachments.Any(x => x.Filename.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task OnDiscordConnected()
    {
        EnsureNotDisposed();

        await FindIndexMessageAsync();
        await PerformInitialSynchronizationAsync();
    }

    private async Task OnDiscordDisconnected(Exception arg)
    {
        EnsureNotDisposed();

        await SetReadyAsync(ready: false);

        _indexMessageId = 0;
        LastKnownRemoteIndex = null;
        _pendingEdits.Clear();
    }

    private async Task OnMessageUpdated(Cacheable<IMessage, ulong> cache, SocketMessage message, ISocketMessageChannel channel)
    {
        if (!_isReady || _disposed)
        {
            return;
        }

        if (!IsIndexDbMessage(message))
        {
            return;
        }

        if (_pendingEdits.Count > 0)
        {
            _pendingEdits.RemoveAt(_pendingEdits.Count - 1);
            return;
        }

        await RetrieveIndexFileAsync(message);
    }

    private async Task PerformInitialSynchronizationAsync()
    {
        EnsureNotDisposed();

        try
        {
            if (_isReady)
            {
                return;
            }

            if (_indexMessageId == 0)
            {
                await PostIndexMessageAsync();
                await SetReadyAsync(ready: true);
                return;
            }

            var indexMessage = await DbChannel.GetMessageAsync(_indexMessageId,
                options: DiscordHelper.CreateDefaultOptions(_cancellationTokenSource.Token));

            await RetrieveIndexFileAsync(indexMessage);
            await SetReadyAsync(ready: true);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, message: "PerformInitialSynchronizationAsync failed");
        }
    }

    private async Task PostIndexMessageAsync(bool buildIndex = false)
    {
        var indexFile = buildIndex
            ? Index.BuildForDirectory(Options.LocalPath)
            : Index.GetEmptyIndex();

        var bytes = indexFile.Serialize();

        using var stream = new MemoryStream(bytes);
        stream.Seek(offset: 0, SeekOrigin.Begin);

        var message = await DbChannel.SendFileAsync(
            text: "**FILE DATABASE**\n"
                  + "\nDO NOT DELETE OR UNPIN THIS MESSAGE."
                  + "\nDO NOT POST ANY OTHER MESSAGES IN THIS CHANNEL.\n"
                  + "\nDoing this will corrupt data or affect performance.",
            attachment: new FileAttachment(stream, IndexFileName),
            options: DiscordHelper.CreateDefaultOptions(_cancellationTokenSource.Token));

        await Task.Delay(millisecondsDelay: 500);
        await message.PinAsync();

        _indexMessageId = message.Id;
        LastKnownRemoteIndex = indexFile;
    }

    private async Task SetReadyAsync(bool ready)
    {
        _isReady = ready;

        if (_isReady)
        {
            _fullResyncTimer.Change(TimeSpan.FromSeconds(value: 2), TimeSpan.FromMinutes(value: 3));
        }
        else
        {
            _fullResyncTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        await StateChange.InvokeAsync(this, new FileProviderStateChangedEventArgs(Status));
    }

    private async Task RetrieveIndexFileAsync(IMessage message)
    {
        EnsureNotDisposed();

        var attachment = message.Attachments.First(x => x.Filename.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase));
        var client = HttpClientFactory.CreateClient(name: "DiscordFS");
        var data = await client.GetByteArrayAsync(attachment.Url, _cancellationTokenSource.Token);

        var fullSync = LastKnownRemoteIndex == null;
        LastKnownRemoteIndex = Index.Deserialize(data);

        if (!fullSync)
        {
            await CheckForChangedEntriesAsync();
        }
    }

    private async Task CheckForChangedEntriesAsync()
    {
        if (LastKnownRemoteIndex == null)
        {
            return;
        }

        var localIndex = Index.BuildForDirectory(Options.LocalPath);
        var remoteIndex = LastKnownRemoteIndex;

        var result = localIndex.CompareTo(remoteIndex);
        foreach (var entry in result.AddedFiles)
        {
            await FileChange.InvokeAsync(this, new FileChangedEventArgs
            {
                ChangeType = FileChangeEventType.Created,
                OldRelativePath = entry.RelativePath,
                Placeholder = new FilePlaceholder(entry),
                ResyncSubDirectories = false
            });
        }

        foreach (var entry in result.DeletedFiles)
        {
            await FileChange.InvokeAsync(this, new FileChangedEventArgs
            {
                ChangeType = FileChangeEventType.Deleted,
                OldRelativePath = entry.RelativePath,
                Placeholder = new FilePlaceholder(entry),
                ResyncSubDirectories = false
            });
        }

        foreach (var entry in result.ModifiedFiles)
        {
            await FileChange.InvokeAsync(this, new FileChangedEventArgs
            {
                ChangeType = FileChangeEventType.Modified,
                OldRelativePath = entry.RelativePath,
                Placeholder = new FilePlaceholder(entry),
                ResyncSubDirectories = false
            });
        }
    }

    public async Task WriteIndexAsync(Index index)
    {
        EnsureNotDisposed();

        if (Status != FileSystemProviderStatus.Ready)
        {
            throw new InvalidOperationException(message: "Status is not ready");
        }

        var bytes = index.Serialize();
        using var stream = new MemoryStream(bytes);
        stream.Seek(offset: 0, SeekOrigin.Begin);

        if (_indexMessageId == 0)
        {
            await FindIndexMessageAsync();
        }

        // index message is gone for some reason
        if (_indexMessageId == 0)
        {
            _logger.LogWarning(message: "Index message is gone, recreating...");
            await PostIndexMessageAsync();
            return;
        }

        var message = (RestUserMessage)await DbChannel.GetMessageAsync(_indexMessageId,
            options: DiscordHelper.CreateDefaultOptions(_cancellationTokenSource.Token));

        await message.ModifyAsync(props =>
        {
            props.Attachments = new Optional<IEnumerable<FileAttachment>>(new[]
            {
                new FileAttachment(stream, IndexFileName)
            });
        });

        await Task.Delay(millisecondsDelay: 1500);

        // Refetch message to get the new edit timestamp
        message = (RestUserMessage)await DbChannel.GetMessageAsync(_indexMessageId,
            options: DiscordHelper.CreateDefaultOptions(_cancellationTokenSource.Token));

        LastKnownRemoteIndex = index;

        _pendingEdits.Add(message.EditedTimestamp!.Value);

        // Remove after 30 seconds if we didnt get edit message event
        _ = Task.Delay(TimeSpan.FromSeconds(value: 30))
            .ContinueWith(_ =>
            {
                _pendingEdits.Remove(message.EditedTimestamp!.Value);
                return Task.CompletedTask;
            });
    }
}