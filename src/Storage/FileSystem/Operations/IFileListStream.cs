using DiscordFS.Storage.FileSystem.Results;

namespace DiscordFS.Storage.FileSystem.Operations;

public interface IFileListStream : IDisposable, IAsyncDisposable
{
    public Task<FileOperationResult> CloseAsync();

    public Task<GetNextFileResult> GetNextAsync();

    public Task<FileOperationResult> OpenAsync(string relativePath, CancellationToken ctx);
}