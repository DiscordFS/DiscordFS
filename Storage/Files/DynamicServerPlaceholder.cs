using DiscordFS.Platforms.Windows.Storage;
using DiscordFS.Storage.Helpers;

namespace DiscordFS.Storage.Files;

public class DynamicServerPlaceholder
{
    private readonly string _relativePath;
    private FilePlaceholder _placeholder;
    private readonly bool _isDirectory;
    private readonly IRemoteFileProvider _remoteFileProvider;

    public DynamicServerPlaceholder(string relativePath, bool isDirectory, IRemoteFileProvider remoteFileProvider)
    {
        _relativePath = relativePath;
        _isDirectory = isDirectory;
        _remoteFileProvider = remoteFileProvider;
    }

    public DynamicServerPlaceholder(FilePlaceholder placeholder)
    {
        _placeholder = placeholder;
    }

    public async Task<FilePlaceholder> GetPlaceholder()
    {
        if (_placeholder == null && !string.IsNullOrWhiteSpace(_relativePath))
        {
            if (!FileExcluder.IsExcludedFile(_relativePath))
            {
                var getFileResult = await _remoteFileProvider.GetFileInfo(_relativePath, _isDirectory);
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