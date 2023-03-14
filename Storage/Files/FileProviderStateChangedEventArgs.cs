namespace DiscordFS.Storage.Files;

public class FileProviderStateChangedEventArgs
{
    public string Message { get; }

    public FileProviderStatus Status { get; }

    public FileProviderStateChangedEventArgs() { }

    public FileProviderStateChangedEventArgs(FileProviderStatus status)
    {
        Status = status;
        Message = status.ToString();
    }

    public FileProviderStateChangedEventArgs(FileProviderStatus status, string message)
    {
        Status = status;
        Message = message;
    }
}