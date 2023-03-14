using System.Text;
using DiscordFS.Storage.Helpers;
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
    public string Version
    {
        get { return "1.0"; }
    }

    public List<IndexEntry> Files { get; set; }

    public Index()
    {
        Files = new List<IndexEntry>();
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
        var addedFiles = other.Files
            .Where(file => !Files.Any(x => x.RelativePath == file.RelativePath))
            .ToList();

        var removedFiles = Files
            .Where(file => !other.Files.Any(x => x.RelativePath == file.RelativePath))
            .ToList();

        var modifiedFiles = Files
            .Where(file => other.Files.Any(x => x.RelativePath == file.RelativePath))
            .Where(file => other.Files.First(x => x.RelativePath == file.RelativePath).LastModificationTime > file.LastModificationTime)
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
        index.Files ??= new List<IndexEntry>();
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

            index.Files.Add(IndexEntry.From(fileInfo, rootDirectory));
        }

        foreach (var directory in Directory.GetDirectories(path))
        {
            index.Files.Add(IndexEntry.From(new DirectoryInfo(directory), rootDirectory));
            RecursiveBuild(index, directory, rootDirectory);
        }
    }
}

public enum IndexEntryType
{
    File,
    Directory
}

public class IndexEntry
{
    public FileAttributes Attributes { get; set; }

    public DateTime CreationTime { get; set; }

    public long FileSize { get; set; }

    public DateTime LastAccessTime { get; set; }

    public DateTime LastModificationTime { get; set; }

    public List<ulong> MessageIds { get; set; }

    public string RelativePath { get; set; }

    public IndexEntryType Type { get; set; }

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
            Type = IndexEntryType.Directory
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
            Type = IndexEntryType.File
        };
    }
}