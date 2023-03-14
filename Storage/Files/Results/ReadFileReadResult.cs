using DiscordFS.Platforms.Windows.Storage;

namespace DiscordFS.Storage.Files.Results;

public class ReadFileReadResult : FileOperationResult
{
    public int BytesRead { get; set; }

    public ReadFileReadResult() { }

    public ReadFileReadResult(int bytesRead)
    {
        BytesRead = bytesRead;
    }

    public ReadFileReadResult(CloudFilterNTStatus status)
    {
        Status = status;
    }
}