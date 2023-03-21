using DiscordFS.Storage.FileSystem.Results;

namespace DiscordFS.Storage.FileSystem.Operations;

public interface IReadFileStream : IDisposable, IAsyncDisposable
{
    public Task<ReadFileCloseResult> CloseAsync();

    public Task<ReadFileOpenResult> OpenAsync(OpenAsyncParams e);

    public Task<ReadFileReadResult> ReadAsync(byte[] buffer, int offsetBuffer, int offset, int count);
}