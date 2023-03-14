using DiscordFS.Platforms.Windows.Storage;

namespace DiscordFS.Storage.Files.Results;

public class WriteFileOpenResult : FileOperationResult
{
    public FilePlaceholder Placeholder { get; set; }

    public WriteFileOpenResult() { }

    public WriteFileOpenResult(FilePlaceholder placeholder)
    {
        Placeholder = placeholder;
    }

    public WriteFileOpenResult(CloudFilterNTStatus status)
    {
        Status = status;
    }
}