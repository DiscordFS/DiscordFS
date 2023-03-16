using System.Diagnostics;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using DiscordFS.Helpers;
using DiscordFS.Platforms.Windows.Helpers;
using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.FileSystem;
using DiscordFS.Storage.FileSystem.Operations;
using DiscordFS.Storage.FileSystem.Results;
using DiscordFS.Storage.Synchronization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using static Vanara.PInvoke.CldApi;
using Index = DiscordFS.Storage.Synchronization.Index;

namespace DiscordFS.Storage.Discord;

public class DiscordRemoteFileSystemProvider : IRemoteFileSystemProvider
{
    private const string IndexFileName = "index.db";
    private const int MaxAttachmentSize = 1024 * 1024 * 8;

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
    private readonly IHttpClientFactory _httpClientFactory;

    // todo:
    private readonly FileRangeManager _fileRangeManager;
    private readonly ILogger _logger;

    private CancellationTokenSource _cancellationTokenSource;
    private ITextChannel _dataChannel;
    private ITextChannel _dbChannel;
    private bool _disposed;
    private SocketGuild _guild;
    private ulong _indexMessageId;
    private bool _isReady;
    private WriteFileCloseResult _lastWriteResult;
    private readonly SemaphoreSlim _uploadLock;
    private Index _uploadIndex;
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
        _httpClientFactory = httpClientFactory;
        _uploadLock = new SemaphoreSlim(initialCount: 1);
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

    public async Task<WriteFileCloseResult> UploadFileAsync(string fullPath, CancellationToken ctx)
    {
        await using var fStream = new FileStream(fullPath, FileMode.Open);

        ctx.ThrowIfCancellationRequested();

        EnsureNotDisposed();

        if (LastKnownRemoteIndex == null || Status != FileSystemProviderStatus.Ready)
        {
            return new WriteFileCloseResult(CloudFileFetchErrorCode.Offline);
        }

        _uploadIndex ??= LastKnownRemoteIndex.Clone();

        // Todo: this will keep the whole file in RAM
        // need to upload as reading file

        using var ms = new MemoryStream();
        await fStream.CopyToAsync(ms, ctx);

        var data = ms.ToArray();
        var chunks = FileChunk.CreateChunks(data, Options.UseCompression, MaxAttachmentSize);

        var messageIds = new List<ulong>();
        foreach (var chunkChunk in chunks.Chunk(size: 10))
        {
            var attachments = new List<FileAttachment>();
            var streams = new DisposableList<Stream>();
            ctx.ThrowIfCancellationRequested();

            foreach (var chunk in chunkChunk)
            {
                // todo: report progress

                ctx.ThrowIfCancellationRequested();
                var fileName = Guid.NewGuid().ToString(format: "N");
                var stream = new MemoryStream(chunk.Data);
                stream.Seek(offset: 0, SeekOrigin.Begin);
                attachments.Add(new FileAttachment(stream, fileName));
                streams.Add(stream);
            }

            var message = await _dataChannel.SendFilesAsync(attachments);
            messageIds.Add(message.Id);
            streams.Dispose();
        }

        ctx.ThrowIfCancellationRequested();

        var relativePath = PathHelper.GetRelativePath(fullPath, Options.LocalPath);
        var fileInfo = new FileInfo(fullPath);

        var entry = _uploadIndex.CreateEmptyFile(relativePath, overwrite: true);
        entry.MessageIds = messageIds;
        entry.Attributes = fileInfo.Attributes;
        entry.FileSize = fileInfo.Length;
        entry.CreationTime = fileInfo.CreationTime;
        entry.LastAccessTime = fileInfo.LastAccessTime;
        entry.LastModificationTime = fileInfo.LastWriteTime;

        await _uploadLock.WaitAsync(ctx);

        try
        {
            ctx.ThrowIfCancellationRequested();

            if (_uploadIndex == null)
            {
                while (_lastWriteResult == null)
                {
                    await Task.Delay(millisecondsDelay: 100, ctx);
                }

                return new WriteFileCloseResult
                {
                    Succeeded = _lastWriteResult.Succeeded,
                    Status = _lastWriteResult.Status,
                    Message = _lastWriteResult.Message,
                    Placeholder = new FilePlaceholder(entry)
                };
            }

            var index = _uploadIndex;
            _uploadIndex = null;

            if (Status != FileSystemProviderStatus.Ready)
            {
                return new WriteFileCloseResult(CloudFileFetchErrorCode.Offline);
            }

            await WriteIndexAsync(index);
            SetInSyncState(fStream.SafeFileHandle, CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);

            var placeholder = new FilePlaceholder(entry);
            return _lastWriteResult = new WriteFileCloseResult
            {
                Succeeded = true,
                Status = CloudFilterNTStatus.STATUS_SUCCESS,
                Placeholder = placeholder
            };
        }
        catch (Exception ex)
        {
            return _lastWriteResult = new WriteFileCloseResult(ex);
        }
        finally
        {
            _uploadLock.Release();
            fStream.Close();
        }
    }

    private static bool SetInSyncState(string fullPath, CF_IN_SYNC_STATE inSyncState, bool isDirectory)
    {
        using var handle = new SafeCreateFileForCldApi(fullPath, isDirectory);

        if (handle.IsInvalid)
        {
            Debug.WriteLine("SetInSyncState INVALID Handle! " + fullPath.TrimEnd(trimChar: '\\'), TraceLevel.Warning);
            return false;
        }

        var result = CfSetInSyncState((SafeFileHandle)handle, inSyncState, CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE);
        return result.Succeeded;
    }

    public static bool SetInSyncState(SafeFileHandle fileHandle, CF_IN_SYNC_STATE inSyncState)
    {
        var res = CfSetInSyncState(fileHandle, inSyncState, CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE);
        return res.Succeeded;
    }

    private async Task EnsureChannelsExistAsync()
    {
        EnsureNotDisposed();

        _guild = _discordClient.GetGuild(ulong.Parse(Options.GuildId));
        _dbChannel = await GetOrCreateChannelAsync(Options.DbChannelName);
        _dataChannel = await GetOrCreateChannelAsync(Options.DataChannelName);

        await FindIndexMessageAsync();

        _ = Task.Run(PerformInitialSynchronizationAsync);
    }

    private async Task FindIndexMessageAsync()
    {
        foreach (var message in await _dbChannel.GetPinnedMessagesAsync())
        {
            if (IsIndexDbMessage(message))
            {
                _indexMessageId = message.Id;
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

            var indexMessage = await _dbChannel.GetMessageAsync(_indexMessageId);
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

        var message = await _dbChannel.SendFileAsync(
            text: "**FILE DATABASE**\n"
                  + "\nDO NOT DELETE OR UNPIN THIS MESSAGE."
                  + "\nDO NOT POST ANY OTHER MESSAGES IN THIS CHANNEL.\n"
                  + "\nDoing this will corrupt data or affect performance.",
            attachment: new FileAttachment(stream, IndexFileName));

        await Task.Delay(millisecondsDelay: 500);
        await message.PinAsync();
        _indexMessageId = message.Id;

        LastKnownRemoteIndex = indexFile;
    }

    private void SetReady(bool ready)
    {
        _isReady = ready;
        StateChange?.Invoke(this, new FileProviderStateChangedEventArgs(Status));
    }

    public async Task<byte[]> DownloadFileAsync(IndexEntry entry, CancellationToken cancellationToken)
    {
        EnsureNotDisposed();

        if (entry.Type == EntryType.Directory)
        {
            throw new Exception(message: "DownloadFileAsync cannot be called on directories");
        }

        if (entry.FileSize == 0 || entry.MessageIds == null || entry.MessageIds.Count == 0)
        {
            return new byte[entry.FileSize];
        }

        cancellationToken.ThrowIfCancellationRequested();

        var attachements = new List<IAttachment>();
        var messages = entry.MessageIds;

        // todo: parallelize this? would we get rate limited? does discord.net even support multi-threading?
        foreach (var messageId in messages)
        {
            _cancellationTokenSource.Token.ThrowIfCancellationRequested();

            var message = await _dbChannel.GetMessageAsync(messageId);
            attachements.AddRange(message.Attachments);
        }

        var chunks = new FileChunk[attachements.Count];
        await Parallel.ForEachAsync(attachements, cancellationToken, async (attachment, token) =>
        {
            var client = _httpClientFactory.CreateClient(name: "DiscordFS");
            var data = await client.GetByteArrayAsync(attachment.Url, token);
            var index = attachements.IndexOf(attachment);
            chunks[index] = FileChunk.Deserialize(data);
        });

        var data = FileChunk.GetFileData(chunks, entry.RelativePath);
        cancellationToken.ThrowIfCancellationRequested();

        return data;
    }

    private async Task RetrieveIndexFileAsync(IMessage message)
    {
        var attachment = message.Attachments.First(x => x.Filename.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase));
        var client = _httpClientFactory.CreateClient(name: "DiscordFS");
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

        /*
        if (_dbChannel is not SocketTextChannel)
        {
            _dbChannel = _guild.GetTextChannel(_dbChannel.Id);
        }
        */

        var message = (RestUserMessage)await _dbChannel.GetMessageAsync(_indexMessageId);
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