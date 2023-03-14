using static Vanara.PInvoke.CldApi;

namespace DiscordFS.Storage.Helpers;

public static class PathHelper
{
    public static string GetAbsolutePath(in CF_CALLBACK_INFO callbackInfo, string rootDirectory)
    {
        var rootDirectoryNormalized = NormalizePath(rootDirectory);
        var relativePath = GetRelativePath(callbackInfo, rootDirectoryNormalized);
        return Path.Combine(rootDirectory, relativePath);
    }

    public static string GetAbsolutePath(string relativePath, string rootDirectory)
    {
        return Path.Combine(rootDirectory, relativePath);
    }

    public static string GetRelativePath(in CF_CALLBACK_PARAMETERS.RENAME callbackInfo, string rootDirectory)
    {
        var rootDirectoryNormalized = NormalizePath(rootDirectory);

        if (callbackInfo.TargetPath.StartsWith(rootDirectoryNormalized, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = callbackInfo.TargetPath.Remove(startIndex: 0, rootDirectoryNormalized.Length);
            return relativePath.TrimStart(char.Parse(s: "\\"));
        }

        return callbackInfo.TargetPath;
    }

    public static string GetRelativePath(string fullPath, string rootDirectory)
    {
        if (!fullPath.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception(message: "File not part of sync directory");
        }

        if (fullPath.Length == rootDirectory.Length)
        {
            return string.Empty;
        }

        return fullPath.Remove(startIndex: 0, rootDirectory.Length + 1);
    }

    public static string GetRelativePath(in CF_CALLBACK_INFO callbackInfo, string rootDirectory)
    {
        var rootDirectoryNormalized = NormalizePath(rootDirectory);

        if (callbackInfo.NormalizedPath.StartsWith(rootDirectoryNormalized, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = callbackInfo.NormalizedPath.Remove(startIndex: 0, rootDirectoryNormalized.Length);
            return relativePath.TrimStart(char.Parse(s: "\\"));
        }

        return callbackInfo.NormalizedPath;
    }

    public static string NormalizePath(string path)
    {
        return path.Remove(startIndex: 0, count: 2);
    }
}