namespace DiscordFS.Platforms.Windows.Storage;

public class BasicFileInfo
{
    public DateTime ChangeTime { get; set; }

    public DateTime CreationTime { get; set; }

    public FileAttributes FileAttributes { get; set; }

    public DateTime LastAccessTime { get; set; }

    public DateTime LastWriteTime { get; set; }
}