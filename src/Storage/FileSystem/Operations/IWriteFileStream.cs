using DiscordFS.Storage.FileSystem.Results;
using DiscordFS.Storage.Synchronization;

namespace DiscordFS.Storage.FileSystem.Operations;

public interface IWriteFileStream : IDisposable, IAsyncDisposable
{
    public UploadMode SupportedUploadModes { get; }

    public Task<WriteFileCloseResult> CloseAsync(bool completed);

    public Task<WriteFileOpenResult> OpenAsync(OpenAsyncParams e);

    public Task<WriteFileWriteResult> WriteAsync(byte[] buffer, int offsetBuffer, long offset, int count);
}