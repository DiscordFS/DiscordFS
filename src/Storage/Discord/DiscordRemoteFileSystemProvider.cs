using Discord;
using Discord.Rest;
using Discord.WebSocket;
using DiscordFS.Storage.FileSystem;
using DiscordFS.Storage.FileSystem.Operations;
using Microsoft.Extensions.Logging;
using Index = DiscordFS.Storage.Synchronization.Index;

namespace DiscordFS.Storage.Discord;

public class DiscordRemoteFileSystemProvider : IRemoteFileSystemProvider
{
    private const string IndexFileName = "index.db";

    public Index LastKnownRemoteIndex { get; protected set; }

    StorageProviderOptions IRemoteFileSystemProvider.Options
    {
        get { return Options; }
    }

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

    public IHttpClientFactory HttpClientFactory { get; private set; }

    // todo:
    private readonly FileRangeManager _fileRangeManager;
    private readonly ILogger _logger;

    private CancellationTokenSource _cancellationTokenSource;

    public ITextChannel DataChannel { get; private set; }

    public ITextChannel DbChannel { get; private set; }

    private bool _disposed;
    private SocketGuild _guild;
    private ulong _indexMessageId;
    private bool _isReady;
    private DateTimeOffset? _lastIndexEditTimestamp;

    public DiscordRemoteFileSystemProvider(
        ILogger logger,
        FileRangeManager fileRangeManager,
        DiscordStorageProviderOptions options,
        DiscordSocketClient discordClient,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _fileRangeManager = fileRangeManager;
        Options = options;
        _discordClient = discordClient;
        HttpClientFactory = httpClientFactory;
        Operations = new DiscordRemoteFileOperations(this);
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

    public event EventHandler<FileProviderStateChangedEventArgs> StateChange;

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

        Task.Run(EnsureChannelsExistAsync);
    }

    private async Task EnsureChannelsExistAsync()
    {
        EnsureNotDisposed();

        _guild = _discordClient.GetGuild(ulong.Parse(Options.GuildId));
        DbChannel = await GetOrCreateChannelAsync(Options.DbChannelName);
        DataChannel = await GetOrCreateChannelAsync(Options.DataChannelName);

        await FindIndexMessageAsync();

        _ = Task.Run(PerformInitialSynchronizationAsync);
    }

    private async Task FindIndexMessageAsync()
    {
        foreach (var message in await DbChannel.GetPinnedMessagesAsync(
                     DiscordHelper.CreateDefaultOptions(_cancellationTokenSource.Token)))
        {
            if (IsIndexDbMessage(message))
            {
                _indexMessageId = message.Id;
                _lastIndexEditTimestamp = message.EditedTimestamp;
            }
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

    private Task OnDiscordDisconnected(Exception arg)
    {
        EnsureNotDisposed();

        SetReady(ready: false);

        _indexMessageId = 0;
        LastKnownRemoteIndex = null;
        _lastIndexEditTimestamp = null;

        return Task.CompletedTask;
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

        if (message.EditedTimestamp == _lastIndexEditTimestamp)
        {
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
                SetReady(ready: true);
                return;
            }

            var indexMessage = await DbChannel.GetMessageAsync(_indexMessageId,
                options: DiscordHelper.CreateDefaultOptions(_cancellationTokenSource.Token));

            SetReady(ready: true);

            await RetrieveIndexFileAsync(indexMessage);
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
            : new Index();

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
        _lastIndexEditTimestamp = message.EditedTimestamp;
        LastKnownRemoteIndex = indexFile;
    }

    private void SetReady(bool ready)
    {
        _isReady = ready;
        StateChange?.Invoke(this, new FileProviderStateChangedEventArgs(Status));
    }

    private async Task RetrieveIndexFileAsync(IMessage message)
    {
        var attachment = message.Attachments.First(x => x.Filename.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase));
        var client = HttpClientFactory.CreateClient(name: "DiscordFS");
        var data = await client.GetByteArrayAsync(attachment.Url, _cancellationTokenSource.Token);

        var remoteIndex = Index.Deserialize(data);
        LastKnownRemoteIndex = remoteIndex;
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

        LastKnownRemoteIndex = index;
        _lastIndexEditTimestamp = message.EditedTimestamp;
    }
}