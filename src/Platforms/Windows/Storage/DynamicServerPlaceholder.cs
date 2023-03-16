using DiscordFS.Storage;
using DiscordFS.Storage.FileSystem;

namespace DiscordFS.Platforms.Windows.Storage;

public class DynamicServerPlaceholder
{
    private readonly string _relativePath;
    private FilePlaceholder _placeholder;
    private readonly bool _isDirectory;
    private readonly IRemoteFileSystemProvider _remoteFileSystemProvider;

    public DynamicServerPlaceholder(string relativePath, bool isDirectory, IRemoteFileSystemProvider remoteFileSystemProvider)
    {
        _relativePath = relativePath;
        _isDirectory = isDirectory;
        _remoteFileSystemProvider = remoteFileSystemProvider;
    }

    public DynamicServerPlaceholder(FilePlaceholder placeholder)
    {
        _placeholder = placeholder;
    }

    public async Task<FilePlaceholder> GetPlaceholderAsync()
    {
        if (_placeholder == null && !string.IsNullOrWhiteSpace(_relativePath))
        {
            if (!FileExcluder.IsExcludedFile(_relativePath))
            {
                var getFileResult = await _remoteFileSystemProvider.Operations.GetFileInfoAsync(_relativePath, _isDirectory);
                _placeholder = getFileResult.Placeholder;

                if (getFileResult.Status == CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE)
                {
                    _placeholder = null;
                }
                else
                {
                    getFileResult.ThrowOnFailure();
                }
            }
        }

        return _placeholder;
    }

    public static explicit operator DynamicServerPlaceholder(FilePlaceholder placeholder)
    {
        return new DynamicServerPlaceholder(placeholder);
    }
}