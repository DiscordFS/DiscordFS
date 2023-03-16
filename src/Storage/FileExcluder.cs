using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DiscordFS.Storage;

public static class FileExcluder
{
    private static readonly string[] FileExclusionPatterns =
    {
        // Files which are not synced. (RegEx)
        @".*\\Thumbs\.db",
        @".*\\Desktop\.ini",
        @".*\.tmp",
        @".*Recycle\.Bin.*",
        @".*\~.*"
    };

    public static bool IsExcludedFile(FileInfo fileInfo)
    {
        return IsExcludedFile(fileInfo.FullName, fileInfo.Attributes);
    }

    public static bool IsExcludedFile(string relativeOrFullPath, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.System) || attributes.HasFlag(FileAttributes.Temporary))
        {
            return true;
        }

        return IsExcludedFile(relativeOrFullPath);
    }

    public static bool IsExcludedFile(string relativeOrFullPath)
    {
        foreach (var pattern in FileExclusionPatterns)
        {
            try
            {
                if (Regex.IsMatch(relativeOrFullPath, pattern, RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debugger.Break();
                throw;
            }
        }

        return false;
    }
}