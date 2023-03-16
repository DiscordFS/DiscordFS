namespace DiscordFS.Storage;

public interface IStorageProvider : IDisposable
{
    public void BeginSynchronization();

    public void EndSychronization(bool disconnect);

    Task RegisterAsync(StorageProviderOptions options);

    void Unregister();
}

public interface IStorageProvider<in TOptions> : IStorageProvider where TOptions : StorageProviderOptions
{
    Task RegisterAsync(TOptions options);
}