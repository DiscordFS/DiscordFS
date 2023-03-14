namespace DiscordFS.Storage.Files;

public class OpenAsyncParams
{
    public CancellationToken CancellationToken { get; set; }

    public string ETag { get; set; }

    public FilePlaceholder FileInfo { get; set; }

    public string RelativeFileName { get; set; }

    public UploadMode UploadMode { get; set; }
}