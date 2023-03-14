using DiscordFS.Storage.Files.Results;

namespace DiscordFS.Storage.Files;

public interface IFileListAsync : IDisposable, IAsyncDisposable
{
    public Task<FileOperationResult> CloseAsync();

    public Task<GetNextFileResult> GetNextAsync();

    public Task<FileOperationResult> OpenAsync(string relativeFileName, CancellationToken ctx);
}