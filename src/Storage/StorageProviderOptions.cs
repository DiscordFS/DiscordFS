using System.Security.Principal;
using System.Text;
using DiscordFS.Helpers;
using JetBrains.Annotations;

namespace DiscordFS.Storage;

public class StorageProviderOptions
{
    [CanBeNull]
    public byte[] EncryptionKey { get; set; }

    public required string LocalPath { get; init; }

    public string LocalPathNormalized
    {
        get { return PathHelper.NormalizePath(LocalPath); }
    }

    public required Guid ProviderId { get; init; }

    public required string ProviderVersion { get; init; }

    public required bool UseCompression { get; set; }

    public string CalculateSyncRootId(string instanceId)
    {
        var sb = new StringBuilder();
        sb.Append(ProviderId);
        sb.Append(value: "!");
        sb.Append(WindowsIdentity.GetCurrent().User!.Value);
        sb.Append(value: "!");
        sb.Append(instanceId);

        return sb.ToString();
    }
}