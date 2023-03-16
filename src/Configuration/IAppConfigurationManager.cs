using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DiscordFS.Configuration;

public interface IAppConfigurationManager
{
    Task<AppConfiguration> GetConfigurationAsync();

    Task WriteConfigurationAsync();
}

public class AppConfigurationManager : IAppConfigurationManager
{
    private readonly string _configPath;
    private AppConfiguration _appConfiguration;

    public AppConfigurationManager()
    {
        _configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path2: "config.yaml");
    }

    public async Task<AppConfiguration> GetConfigurationAsync()
    {
        if (_appConfiguration != null)
        {
            return _appConfiguration;
        }

        if (!File.Exists(_configPath))
        {
            return _appConfiguration = GetDefaultConfiguration();
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var yaml = await File.ReadAllTextAsync(_configPath);
        _appConfiguration = deserializer.Deserialize<AppConfiguration>(yaml) ?? GetDefaultConfiguration();
        return _appConfiguration;
    }

    public async Task WriteConfigurationAsync()
    {
        _appConfiguration ??= GetDefaultConfiguration();

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var yaml = serializer.Serialize(_appConfiguration);
        await File.WriteAllTextAsync(_configPath, yaml);
    }

    private AppConfiguration GetDefaultConfiguration()
    {
        return new AppConfiguration();
    }
}