using DiscordFS.Storage.Files.Results;

namespace DiscordFS.Storage.Files;

public interface IReadFileAsync : IDisposable, IAsyncDisposable
{
    public Task<ReadFileCloseResult> CloseAsync();

    public Task<ReadFileOpenResult> OpenAsync(OpenAsyncParams e);

    public Task<ReadFileReadResult> ReadAsync(byte[] buffer, int offsetBuffer, long offset, int count);
}