using Discord;
using Discord.WebSocket;
using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.Files;
using DiscordFS.Storage.Files.Results;
using DiscordFS.Storage.Helpers;
using Microsoft.Extensions.Logging;

namespace DiscordFS.Storage.Discord;

public class DiscordRemoteFileProvider : IRemoteFileProvider
{
    private const string IndexFileName = "index.db";

    // Buffer size for P/Invoke Call to CFExecute (max 1 MB)
    private const int StackSize = 1024 * 512;

    public Index LastKnownRemoteIndex { get; protected set; }

    StorageProviderOptions IRemoteFileProvider.Options
    {
        get { return Options; }
    }

    public DiscordStorageProviderOptions Options { get; }

    public FileProviderStatus Status
    {
        get
        {
            return _isReady
                   && _discordClient.ConnectionState == ConnectionState.Connected
                ? FileProviderStatus.Ready
                : FileProviderStatus.NotReady;
        }
    }

    private readonly DiscordSocketClient _discordClient;
    private readonly FileRangeManager _fileRangeManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    private CancellationTokenSource _cancellationTokenSource;
    private ITextChannel _dataChannel;
    private ITextChannel _dbChannel;
    private bool _disposed;
    private SocketGuild _guild;
    private ulong _indexMessageId;
    private bool _isReady;

    public DiscordRemoteFileProvider(
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

    public Task<CreateFileResult> CreateFileAsync(string relativeFileName, bool isDirectory)
    {
        var createFileResult = new CreateFileResult();

        var fullPath = PathHelper.GetAbsolutePath(relativeFileName, Options.LocalPath);

        try
        {
            if (isDirectory)
            {
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }
            }
            else
            {
                if (File.Exists(fullPath))
                {
                    createFileResult.Status = CloudFilterNTStatus.STATUS_CLOUD_FILE_IN_USE;
                    createFileResult.Message = "File already exists";
                    createFileResult.Succeeded = false;
                }
                else
                {
                    using var strm = File.Create(fullPath);
                    strm.Close();
                }
            }

            createFileResult.FilePlaceholder = new FilePlaceholder(fullPath);
        }
        catch (Exception ex)
        {
            createFileResult.SetException(ex);
        }

        return Task.FromResult(createFileResult);
    }

    public Task<DeleteFileResult> DeleteFileAsync(string relativeFileName, bool isDirectory)
    {
        var deleteFileResult = new DeleteFileResult();

        try
        {
            var fullPath = PathHelper.GetAbsolutePath(relativeFileName, Options.LocalPath);
            if (isDirectory)
            {
                Directory.Delete(fullPath, recursive: false);
            }
            else
            {
                File.Delete(fullPath);
            }
        }
        catch (DirectoryNotFoundException ex)
        {
            // Directory already deleted?
            deleteFileResult.Message = ex.Message;
        }
        catch (FileNotFoundException ex)
        {
            // File already deleted?
            deleteFileResult.Message = ex.Message;
        }
        catch (Exception ex)
        {
            deleteFileResult.SetException(ex);
        }

        return Task.FromResult(deleteFileResult);
    }


    public Task<GetFileInfoResult> GetFileInfo(string relativeFileName, bool isDirectory)
    {
        var getFileInfoResult = new GetFileInfoResult();

        var fullPath = PathHelper.GetAbsolutePath(relativeFileName, Options.LocalPath);

        try
        {
            if (isDirectory)
            {
                if (!Directory.Exists(fullPath))
                {
                    return Task.FromResult(new GetFileInfoResult(CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE));
                }
            }
            else
            {
                if (!File.Exists(fullPath))
                {
                    return Task.FromResult(new GetFileInfoResult(CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE));
                }
            }

            getFileInfoResult.Placeholder = new FilePlaceholder(fullPath);
        }
        catch (Exception ex)
        {
            getFileInfoResult.SetException(ex);
        }

        return Task.FromResult(getFileInfoResult);
    }

    public IFileListAsync GetNewFileList()
    {
        return new GetFileListAsync(this);
    }

    public IReadFileAsync GetNewReadFile()
    {
        return new ReadFileAsync(this);
    }

    public IWriteFileAsync GetNewWriteFile()
    {
        return new WriteFileAsync(this);
    }

    public Task<MoveFileResult> MoveFileAsync(string relativeFileName, string relativeDestination, bool isDirectory)
    {
        var fullPath = PathHelper.GetAbsolutePath(relativeFileName, Options.LocalPath);
        var fullPathDestination = PathHelper.GetAbsolutePath(relativeDestination, Options.LocalPath);

        if (!Directory.Exists(Path.GetDirectoryName(fullPathDestination)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPathDestination)!);
        }

        var moveFileResult = new MoveFileResult();

        try
        {
            if (isDirectory)
            {
                Directory.Move(fullPath, fullPathDestination);
            }
            else
            {
                File.Move(fullPath, fullPathDestination);
            }
        }
        catch (Exception ex)
        {
            moveFileResult.SetException(ex);
        }

        return Task.FromResult(moveFileResult);
    }

    public Task<WriteFileCloseResult> UploadFileToServerAsync(string fullPath, CancellationToken ctx)
    {
        // todo
        return Task.FromResult(new WriteFileCloseResult());
    }

    private async Task<byte[]> DownloadFileAsync(IndexEntry entry, CancellationToken cancellationToken)
    {
        EnsureNotDisposed();

        if (entry.Type == IndexEntryType.Directory)
        {
            throw new Exception(message: "DownloadFileAsync cannot be called on directories");
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
            chunks[index] = FileChunk.From(data);
        });

        var data = FileChunk.GetFileData(chunks, entry.RelativePath);
        cancellationToken.ThrowIfCancellationRequested();

        return data;
    }

    private async Task EnsureChannelsExistAsync()
    {
        EnsureNotDisposed();

        _guild = _discordClient.GetGuild(ulong.Parse(Options.GuildId));
        _dbChannel = await GetOrCreateChannelAsync(Options.DbChannelName);
        _dataChannel = await GetOrCreateChannelAsync(Options.DataChannelName);

        foreach (var message in await _dbChannel.GetPinnedMessagesAsync())
        {
            if (IsIndexDbMessage(message))
            {
                _indexMessageId = message.Id;
            }
        }

        _ = Task.Run(PerformInitialSynchronizationAsync);
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DiscordRemoteFileProvider));
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
                var indexFile = Index.BuildForDirectory(Options.LocalPath);
                var bytes = indexFile.Serialize();

                using var stream = new MemoryStream(bytes);
                stream.Seek(offset: 0, SeekOrigin.Begin);

                var message = await _dbChannel.SendFileAsync(
                    text: "**FILE DATABASE**\n"
                          + "\nDO NOT DELETE OR UNPIN THIS MESSAGE."
                          + "\nDO NOT POST ANY OTHER MESSAGES IN THIS CHANNEL.\n"
                          + "\nDoing this may corrupt data.",
                    attachment: new FileAttachment(stream, IndexFileName));

                await Task.Delay(millisecondsDelay: 500);
                await message.PinAsync();
                _indexMessageId = message.Id;
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

    private void SetReady(bool ready)
    {
        _isReady = ready;
        StateChange?.Invoke(this, new FileProviderStateChangedEventArgs(Status));
    }

    private async Task RetrieveIndexFileAsync(IMessage message)
    {
        var attachment = message.Attachments.First(x => x.Filename.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase));
        var client = _httpClientFactory.CreateClient(name: "DiscordFS");
        var data = await client.GetByteArrayAsync(attachment.Url, _cancellationTokenSource.Token);

        var remoteIndex = Index.Deserialize(data);
        LastKnownRemoteIndex = remoteIndex;
    }
}