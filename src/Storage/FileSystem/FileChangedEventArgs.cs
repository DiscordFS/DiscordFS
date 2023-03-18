using DiscordFS.Platforms.Windows.Storage;

namespace DiscordFS.Storage.FileSystem;

public class FileChangedEventArgs
{
    public bool ResyncSubDirectories { get; set; }

    public string OldRelativePath { get; set; }

    public FilePlaceholder Placeholder { get; set; }

    public FileChangeEventType ChangeType { get; set; }
}