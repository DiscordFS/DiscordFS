using Vanara.PInvoke;

namespace DiscordFS.Storage.Actions;

public class DeleteAction
{
    public CldApi.CF_OPERATION_INFO OperationInfo { get; set; }

    public string RelativePath { get; set; }

    public bool IsDirectory { get; set; }
}