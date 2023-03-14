namespace DiscordFS.Storage.Helpers;

public class DisposableList<T> : List<T>, IDisposable where T : IDisposable
{
    public void Dispose()
    {
        foreach (var obj in this)
        {
            obj.Dispose();
        }
    }
}