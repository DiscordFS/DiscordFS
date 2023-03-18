using DiscordFS.Storage.Synchronization;

namespace DiscordFS.Platforms.Windows.Storage;

public class FilePlaceholder : BasicFileInfo
{
    public string ETag;
    public long FileSize;

    public string RelativeFileName;

    public FilePlaceholder(FileSystemInfo fileInfo)
    {
        RelativeFileName = fileInfo.Name;
        FileSize = fileInfo.Attributes.HasFlag(FileAttributes.Directory)
            ? 0
            : ((FileInfo)fileInfo).Length;
        FileAttributes = fileInfo.Attributes;
        CreationTime = fileInfo.CreationTime;
        LastWriteTime = fileInfo.LastWriteTime;
        LastAccessTime = fileInfo.LastAccessTime;
        ChangeTime = fileInfo.LastWriteTime;
        ETag = "_" + fileInfo.LastWriteTime.ToUniversalTime().Ticks + "_" + FileSize;
    }

    public FilePlaceholder(string fullPath)
    {
        var fileInfo = new FileInfo(fullPath);

        RelativeFileName = fileInfo.Name;
        FileSize = fileInfo.Attributes.HasFlag(FileAttributes.Directory)
            ? 0
            : fileInfo.Length;
        FileAttributes = fileInfo.Attributes;
        CreationTime = fileInfo.CreationTime;
        LastWriteTime = fileInfo.LastWriteTime;
        LastAccessTime = fileInfo.LastAccessTime;
        ChangeTime = fileInfo.LastWriteTime;
        ETag = "_" + fileInfo.LastWriteTime.ToUniversalTime().Ticks + "_" + FileSize;
    }

    public FilePlaceholder(string fullPath, string relativeFileName)
    {
        var fileInfo = new FileInfo(fullPath);

        RelativeFileName = relativeFileName;
        FileSize = fileInfo.Attributes.HasFlag(FileAttributes.Directory)
            ? 0
            : fileInfo.Length;
        FileAttributes = fileInfo.Attributes;
        CreationTime = fileInfo.CreationTime;
        LastWriteTime = fileInfo.LastWriteTime;
        LastAccessTime = fileInfo.LastAccessTime;
        ChangeTime = fileInfo.LastWriteTime;
        ETag = "_" + fileInfo.LastWriteTime.ToUniversalTime().Ticks + "_" + FileSize;
    }

    public FilePlaceholder(string relativeFileName, bool isDirectory)
    {
        RelativeFileName = relativeFileName;
        FileAttributes = isDirectory
            ? FileAttributes.Directory
            : FileAttributes.Normal;
    }

    public FilePlaceholder(IndexEntry indexEntry)
    {
        RelativeFileName = indexEntry.RelativePath;
        FileSize = indexEntry.Attributes.HasFlag(FileAttributes.Directory)
            ? 0
            : indexEntry.FileSize;
        FileAttributes = indexEntry.Attributes;
        CreationTime = indexEntry.CreationTime.LocalDateTime;
        LastWriteTime = indexEntry.LastModificationTime.LocalDateTime;
        LastAccessTime = indexEntry.LastAccessTime.LocalDateTime;
        ChangeTime = indexEntry.LastModificationTime.LocalDateTime;
        ETag = "_" + indexEntry.LastModificationTime.ToUniversalTime().Ticks + "_" + FileSize;
    }
}