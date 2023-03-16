using DiscordFS.Storage.FileSystem.Results;

namespace DiscordFS.Storage.FileSystem.Operations;

public interface IFileListAsync : IDisposable, IAsyncDisposable
{
    public Task<FileOperationResult> CloseAsync();

    public Task<GetNextFileResult> GetNextAsync();

    public Task<FileOperationResult> OpenAsync(string relativeFileName, CancellationToken ctx);
}