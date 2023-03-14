namespace DiscordFS.Storage.Files.Results;

public class WriteFileCloseResult : FileOperationResult
{
    public FilePlaceholder Placeholder { get; set; }
}