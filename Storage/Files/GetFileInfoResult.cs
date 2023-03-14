using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.Files.Results;

namespace DiscordFS.Storage.Files;

public class GetFileInfoResult : FileOperationResult
{
    public FilePlaceholder Placeholder { get; set; }

    public GetFileInfoResult() { }

    public GetFileInfoResult(CloudFilterNTStatus status)
    {
        Status = status;
    }
}