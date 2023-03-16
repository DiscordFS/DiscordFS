using DiscordFS.Platforms.Windows.Storage;

namespace DiscordFS.Storage.FileSystem.Results;

public class GetFileInfoResult : FileOperationResult
{
    public FilePlaceholder Placeholder { get; set; }

    public GetFileInfoResult() { }

    public GetFileInfoResult(CloudFileFetchErrorCode error) : base(error) { }

    public GetFileInfoResult(CloudFilterNTStatus status) : base(status) { }
}