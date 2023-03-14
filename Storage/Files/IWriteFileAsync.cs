using DiscordFS.Storage.Files.Results;

namespace DiscordFS.Storage.Files;

public interface IWriteFileAsync : IDisposable, IAsyncDisposable
{
    public UploadMode SupportedUploadModes { get; }

    public Task<WriteFileCloseResult> CloseAsync(bool completed);

    public Task<WriteFileOpenResult> OpenAsync(OpenAsyncParams e);

    public Task<WriteFileWriteResult> WriteAsync(byte[] buffer, int offsetBuffer, long offset, int count);
}