namespace DiscordFS.Storage.FileSystem;

public class FileProviderStateChangedEventArgs
{
    public string Message { get; }

    public FileSystemProviderStatus Status { get; }

    public FileProviderStateChangedEventArgs() { }

    public FileProviderStateChangedEventArgs(FileSystemProviderStatus status)
    {
        Status = status;
        Message = status.ToString();
    }

    public FileProviderStateChangedEventArgs(FileSystemProviderStatus status, string message)
    {
        Status = status;
        Message = message;
    }
}