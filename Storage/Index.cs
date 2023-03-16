using System.Text;
using DiscordFS.Storage.Helpers;
using JetBrains.Annotations;
using K4os.Compression.LZ4;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DiscordFS.Storage;

public class FileIndexCompareResult
{
    public ICollection<IndexEntry> AddedFiles { get; }

    public ICollection<IndexEntry> ModifiedFiles { get; }

    public ICollection<IndexEntry> RemovedFiles { get; }

    public FileIndexCompareResult(
        ICollection<IndexEntry> removedFiles,
        ICollection<IndexEntry> addedFiles,
        ICollection<IndexEntry> modifiedFiles)
    {
        RemovedFiles = removedFiles;
        AddedFiles = addedFiles;
        ModifiedFiles = modifiedFiles;
    }
}

public class Index
{
    public string Version { get; set; } = "1.0";

    public Index Clone()
    {
        var index = new Index();
        foreach (var entry in Entries.ToList())
        {
            _entries.Add(entry.Clone());
        }

        return index;
    }

    private List<IndexEntry> _entries;

    public ICollection<IndexEntry> Entries
    {
        get { return _entries.AsReadOnly(); }
        [UsedImplicitly] private set { _entries = value.ToList(); }
    }

    public Index()
    {
        _entries = new List<IndexEntry>();
    }

    public static Index BuildForDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Directory {directory} does not exist.");
        }

        var index = new Index();
        RecursiveBuild(index, directory, directory);
        return index;
    }

    public FileIndexCompareResult CompareTo(Index other)
    {
        var addedFiles = other.Entries
            .Where(file => !Entries.Any(x => PathHelper.Equals(x.RelativePath, file.RelativePath)))
            .ToList();

        var removedFiles = Entries
            .Where(file => !other.Entries.Any(x => PathHelper.Equals(x.RelativePath, file.RelativePath)))
            .ToList();

        var modifiedFiles = Entries
            .Where(file => other.Entries.Any(x => PathHelper.Equals(x.RelativePath, file.RelativePath)))
            .Where(file => other.Entries.First(x => PathHelper.Equals(x.RelativePath, file.RelativePath)).LastModificationTime
                           > file.LastModificationTime)
            .ToList();

        return new FileIndexCompareResult(
            removedFiles,
            addedFiles,
            modifiedFiles
        );
    }

    public static Index Deserialize(byte[] data)
    {
        const int offset = sizeof(int);
        var decompressedSize = BitConverter.ToInt32(data, startIndex: 0);
        var textBytes = new byte[decompressedSize];
        var count = LZ4Codec.Decode(
            data,
            offset,
            data.Length - offset,
            textBytes,
            targetOffset: 0,
            textBytes.Length);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var yaml = Encoding.UTF8.GetString(textBytes, index: 0, count);
        var index = deserializer.Deserialize<Index>(yaml);
        index._entries ??= new List<IndexEntry>();
        return index;
    }

    public byte[] Serialize()
    {
        const int offset = sizeof(int);
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
            .Build();

        var yaml = serializer.Serialize(this);

        var textBytes = Encoding.UTF8.GetBytes(yaml);
        var sizeBytes = BitConverter.GetBytes(textBytes.Length);

        var target = new byte[LZ4Codec.MaximumOutputSize(textBytes.Length)];
        var count = LZ4Codec.Encode(textBytes, sourceOffset: 0, textBytes.Length, target, targetOffset: 0, target.Length, LZ4Level.L12_MAX);

        var result = new byte[count + offset];

        Buffer.BlockCopy(sizeBytes, srcOffset: 0, result, dstOffset: 0, sizeBytes.Length);
        Buffer.BlockCopy(target, srcOffset: 0, result, offset, count);
        return result;
    }

    private static void RecursiveBuild(Index index, string path, string rootDirectory)
    {
        foreach (var file in Directory.GetFiles(path))
        {
            var fileInfo = new FileInfo(file);
            if (FileExcluder.IsExcludedFile(fileInfo))
            {
                continue;
            }

            index._entries.Add(IndexEntry.From(fileInfo, rootDirectory));
        }

        foreach (var directory in Directory.GetDirectories(path))
        {
            index._entries.Add(IndexEntry.From(new DirectoryInfo(directory), rootDirectory));
            RecursiveBuild(index, directory, rootDirectory);
        }
    }

    public bool FileExists(string relativePath)
    {
        return GetEntry(relativePath, EntryType.File) != null;
    }

    public bool DirectoryExists(string relativePath)
    {
        return GetEntry(relativePath, EntryType.Directory) != null;
    }

    public IndexEntry CreateDirectory(string relativePath)
    {
        var directory = GetEntry(relativePath, EntryType.Directory);
        if (directory != null)
        {
            return directory;
        }

        return CreateDirectoryInternal(relativePath, createSubdirectories: true);
    }

    private IndexEntry CreateDirectoryInternal(string relativePath, bool createSubdirectories)
    {
        if (createSubdirectories)
        {
            var directories = relativePath.Replace(oldValue: "\\", newValue: "/").Split(separator: '/');
            var current = directories[0];
            var i = 0;

            // Create sub directories
            foreach (var directory in directories)
            {
                if (i == directories.Length - 1)
                {
                    break;
                }

                if (!DirectoryExists(current))
                {
                    CreateDirectoryInternal(current, createSubdirectories: false);
                }

                current = Path.Combine(current, directory);
            }
        }

        var entry = new IndexEntry
        {
            Type = EntryType.Directory,
            Attributes = FileAttributes.Directory,
            RelativePath = relativePath,
            CreationTime = DateTime.Now,
            FileSize = 0,
            LastAccessTime = DateTime.Now,
            LastModificationTime = DateTime.Now
        };

        _entries.Add(entry);
        return entry;
    }

    public IndexEntry GetFile(string relativePath)
    {
        return GetEntry(relativePath, EntryType.File);
    }

    public IndexEntry GetDirectory(string relativePath)
    {
        return GetEntry(relativePath, EntryType.Directory);
    }

    private IndexEntry GetEntry(string relativePath, EntryType entryType)
    {
        return Entries.FirstOrDefault(x => PathHelper.Equals(x.RelativePath, relativePath) && x.Type == entryType);
    }

    public bool RemoveFile(string relativePath)
    {
        return RemoveEntry(relativePath, EntryType.File);
    }

    public bool RemoveDirectory(string relativePath)
    {
        return RemoveEntry(relativePath, EntryType.Directory);
    }

    private bool RemoveEntry(string relativePath, EntryType entryType)
    {
        var entry = GetEntry(relativePath, entryType);
        if (entry == null)
        {
            return false;
        }

        if (entry.Type == EntryType.Directory)
        {
            // Remove directory content
            _entries.RemoveAll(x => x.RelativePath.StartsWith(relativePath));
        }

        _entries.Remove(entry);
        return true;
    }

    private void MoveEntry(string relativeFileName, string relativeDestination, EntryType entryType)
    {
        var sourceEntry = GetEntry(relativeFileName, entryType);
        if (entryType == EntryType.File)
        {
            if (FileExists(relativeDestination))
            {
                throw new IOException(message: "Target file already exists");
            }

            var destinationDirectory = Path.GetDirectoryName(relativeDestination);
            if (!DirectoryExists(destinationDirectory))
            {
                CreateDirectory(destinationDirectory);
            }
        }
        else
        {
            if (DirectoryExists(relativeDestination))
            {
                throw new IOException(message: "Target directory already exists");
            }

            if (!DirectoryExists(relativeDestination))
            {
                var topDirectory = CreateDirectory(relativeDestination);

                // We just care about sub directories
                _entries.Remove(topDirectory);
            }

            // move content
            foreach (var file in Entries)
            {
                if (file.RelativePath.StartsWith(relativeFileName))
                {
                    file.RelativePath = file.RelativePath.Replace(relativeFileName, relativeDestination);
                }
            }
        }

        sourceEntry.RelativePath = relativeDestination;
    }

    public void MoveDirectory(string relativeFileName, string relativeDestination)
    {
        MoveEntry(relativeFileName, relativeDestination, EntryType.Directory);
    }

    public void MoveFile(string relativeFileName, string relativeDestination)
    {
        MoveEntry(relativeFileName, relativeDestination, EntryType.File);
    }

    public IndexEntry CreateEmptyFile(string relativeFileName, bool overwrite = false)
    {
        var file = GetFile(relativeFileName);

        if (file != null)
        {
            if (!overwrite)
            {
                throw new IOException($"File already exists: {relativeFileName}");
            }

            _entries.Remove(file);
        }

        var directory = Path.GetDirectoryName(relativeFileName);
        if (!DirectoryExists(directory))
        {
            CreateDirectory(directory);
        }

        var entry = new IndexEntry
        {
            Type = EntryType.File,
            RelativePath = relativeFileName,
            Attributes = FileAttributes.Normal,
            CreationTime = DateTime.Now,
            FileSize = 0,
            LastAccessTime = DateTime.Now,
            LastModificationTime = DateTime.Now
        };

        _entries.Add(entry);
        return entry;
    }
}

public enum EntryType
{
    File,
    Directory
}

public class IndexEntry
{
    public IndexEntry() { }

    private IndexEntry(IndexEntry @base)
    {
        Attributes = @base.Attributes;
        CreationTime = @base.CreationTime;
        FileSize = @base.FileSize;
        LastAccessTime = @base.LastAccessTime;
        LastModificationTime = @base.LastModificationTime;
        MessageIds = @base.MessageIds?.ToList() ?? new List<ulong>();
        RelativePath = @base.RelativePath;
        Type = @base.Type;
    }

    public FileAttributes Attributes { get; set; }

    public DateTime CreationTime { get; set; }

    public long FileSize { get; set; }

    public DateTime LastAccessTime { get; set; }

    public DateTime LastModificationTime { get; set; }

    public List<ulong> MessageIds { get; set; }

    public string RelativePath { get; set; }

    public EntryType Type { get; set; }

    public static IndexEntry From(DirectoryInfo directoryInfo, string syncDirectory)
    {
        return new IndexEntry
        {
            Attributes = directoryInfo.Attributes,
            CreationTime = directoryInfo.CreationTime,
            FileSize = 0,
            LastAccessTime = directoryInfo.LastAccessTime,
            LastModificationTime = directoryInfo.LastWriteTime,
            RelativePath = PathHelper.GetRelativePath(directoryInfo.FullName, syncDirectory),
            Type = EntryType.Directory
        };
    }

    public static IndexEntry From(FileInfo fileInfo, string syncDirectory)
    {
        return new IndexEntry
        {
            Attributes = fileInfo.Attributes,
            CreationTime = fileInfo.CreationTime,
            FileSize = fileInfo.Length,
            LastAccessTime = fileInfo.LastAccessTime,
            LastModificationTime = fileInfo.LastWriteTime,
            RelativePath = PathHelper.GetRelativePath(fileInfo.FullName, syncDirectory),
            Type = EntryType.File
        };
    }

    public IndexEntry Clone()
    {
        return new IndexEntry(this);
    }
}