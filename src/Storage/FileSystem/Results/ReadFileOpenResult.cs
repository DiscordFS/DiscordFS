using DiscordFS.Platforms.Windows.Storage;

namespace DiscordFS.Storage.FileSystem.Results;

public class ReadFileOpenResult : FileOperationResult
{
    public FilePlaceholder Placeholder { get; set; }

    public ReadFileOpenResult() { }

    public ReadFileOpenResult(FilePlaceholder placeholder)
    {
        Placeholder = placeholder;
    }

    public ReadFileOpenResult(CloudFilterNTStatus status) : base(status) { }

    public ReadFileOpenResult(CloudFileFetchErrorCode error) : base(error) { }
}