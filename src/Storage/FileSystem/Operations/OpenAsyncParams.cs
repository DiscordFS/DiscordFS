using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.Synchronization;

namespace DiscordFS.Storage.FileSystem.Operations;

public class OpenAsyncParams
{
    public CancellationToken CancellationToken { get; set; }

    public string ETag { get; set; }

    public FilePlaceholder FileInfo { get; set; }

    public string RelativePath { get; set; }

    public UploadMode UploadMode { get; set; }
}