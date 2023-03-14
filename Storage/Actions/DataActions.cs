using Vanara.PInvoke;

namespace DiscordFS.Storage.Actions;

public class DataActions
{
    public CancellationTokenSource CancellationTokenSource { get; set; }

    public long FileOffset { get; set; }

    public Guid Guid { get; set; } = Guid.NewGuid();

    public string Id { get; set; }

    public bool IsCompleted { get; set; }

    public long Length { get; set; }

    public string NormalizedPath { get; set; }

    public byte PriorityHint { get; set; }

    public CldApi.CF_REQUEST_KEY RequestKey { get; set; }

    public CldApi.CF_TRANSFER_KEY TransferKey { get; set; }
}