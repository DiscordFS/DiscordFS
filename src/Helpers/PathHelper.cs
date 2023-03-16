using static Vanara.PInvoke.CldApi;

namespace DiscordFS.Helpers;

public static class PathHelper
{
    public static bool Equals(string path1, string path2)
    {
        if (path1 == null || path2 == null)
        {
            return false;
        }

        return path1.Trim(trimChar: '\\')
            .Trim(trimChar: '/')
            .Equals(path2.Trim('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

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
        if (rootDirectory.Contains(value: ":"))
        {
            rootDirectory = NormalizePath(rootDirectory);
        }

        if (callbackInfo.TargetPath.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = callbackInfo.TargetPath.Remove(startIndex: 0, rootDirectory.Length);
            return relativePath.TrimStart(char.Parse(s: "\\"));
        }

        return callbackInfo.TargetPath;
    }

    public static string GetRelativePath(string fullPath, string rootDirectory)
    {
        if (!rootDirectory.Contains(value: ":") && fullPath.Contains(value: ":"))
        {
            fullPath = NormalizePath(fullPath);
        }

        if (!fullPath.Contains(value: ":") && rootDirectory.Contains(value: ":"))
        {
            rootDirectory = NormalizePath(rootDirectory);
        }

        if (!fullPath.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception(message: "File not part of sync directory");
        }

        if (fullPath.Length == rootDirectory.Length)
        {
            return string.Empty;
        }

        return fullPath
            .Remove(startIndex: 0, rootDirectory.Length + 1)
            .TrimStart(char.Parse(s: "\\"));
    }

    public static string GetRelativePath(in CF_CALLBACK_INFO callbackInfo, string rootDirectory)
    {
        if (rootDirectory.Contains(value: ":"))
        {
            rootDirectory = NormalizePath(rootDirectory);
        }

        if (callbackInfo.NormalizedPath.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = callbackInfo.NormalizedPath.Remove(startIndex: 0, rootDirectory.Length);
            return relativePath.TrimStart(char.Parse(s: "\\"));
        }

        return callbackInfo.NormalizedPath;
    }

    public static string NormalizePath(string path)
    {
        if (!path.Contains(value: ":"))
        {
            return path;
        }

        return path.Remove(startIndex: 0, count: 2);
    }
}