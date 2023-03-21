using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.WebSocket;
using DiscordFS.Configuration;
using DiscordFS.Storage.Discord;
using Microsoft.Extensions.Logging;

namespace DiscordFS;

public partial class MainPage
{
    private IAppConfigurationManager _configurationManager;
    private DiscordSocketClient _discordClient;
    private ILogger<MainPage> _logger;
    private IDiscordStorageProvider _storageProvider;

    public MainPage()
    {
        InitializeComponent();
    }

    private async Task<string> GetBotTokenAsync(AppConfiguration config)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(config.Discord?.BotToken))
            {
                config.Discord ??= new DiscordConfiguration();
                config.Discord.BotToken = await DisplayPromptAsync(
                    title: "Bot token",
                    message: "Please enter bot token:",
                    accept: "Connect",
                    cancel: null);
            }

            await ValidateDiscordTokenAndConnectAsync(config.Discord.BotToken);
            return config.Discord.BotToken;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, message: null);

            await DisplayAlert(title: "Invalid token", message: "Failed to connect to the bot. Please try again.", cancel: "OK");
            config.Discord!.BotToken = null;
            return await GetBotTokenAsync(config);
        }
    }

    private string GetDataChannel(AppConfiguration config)
    {
        config.Discord ??= new DiscordConfiguration();

        if (string.IsNullOrWhiteSpace(config.Discord.DataChannel))
        {
            config.Discord.DataChannel = "#data";
        }

        return config.Discord.DataChannel;
    }

    private string GetDbChannel(AppConfiguration config)
    {
        config.Discord ??= new DiscordConfiguration();

        if (string.IsNullOrWhiteSpace(config.Discord.DbChannel))
        {
            config.Discord.DbChannel = "#db";
        }

        return config.Discord.DbChannel;
    }

    private async Task<byte[]> GetEncryptionKeyAsync(AppConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.EncryptionKey))
        {
            if (config.EncryptionKey.ToLowerInvariant().Trim() == "none")
            {
                return null;
            }

            return Convert.FromBase64String(config.EncryptionKey);
        }

        var addEncryptionKey = await DisplayAlert(
            title: "Encryption setup"
            , message: "Would you like to set up encryption? This cannot be changed later.",
            accept: "Yes",
            cancel: "No");

        if (!addEncryptionKey)
        {
            config.EncryptionKey = "none";
            return null;
        }

        var password = await DisplayPromptAsync(
            title: "Encryption setup",
            message: "Please enter your desired encryption password:",
            accept: "Save",
            cancel: null);

        var key = Encoding.UTF8.GetBytes(password);
        using (var sha2 = SHA256.Create())
            key = sha2.ComputeHash(key);

        config.EncryptionKey = Convert.ToBase64String(key);

        await DisplayAlert(title: "Encryption",
            "Your password has been set up.\n" +
            "If you want to set up multiple devices you will have to use the same password", cancel: "OK");

        return key;
    }

    private async Task<string> GetGuildIdAsync(AppConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.Discord?.GuildId))
        {
            config.Discord ??= new DiscordConfiguration();
            config.Discord.GuildId = await DisplayPromptAsync(
                title: "Guild",
                message: "Which guild should the bot use? Enter GuildId:",
                accept: "Save",
                cancel: null);
        }

        var guild = _discordClient.GetGuild(ulong.Parse(config.Discord.GuildId));
        if (guild != null)
        {
            return config.Discord.GuildId;
        }

        await DisplayAlert(title: "Invalid Guild", message: "The given guild was not found. Please try again.", cancel: "OK");
        config.Discord.GuildId = null;
        return await GetGuildIdAsync(config);
    }

    private string GetLocalPath(AppConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.LocalSyncPath))
        {
            var defaultDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path2: "DiscordFS");
            config.LocalSyncPath = defaultDirectory;
        }

        return config.LocalSyncPath;
    }

    private void OnAppIsReady()
    {
        ContentLayout.IsVisible = true;
        LoadingLayout.IsVisible = false;
    }

    private async Task OnAppNotSupported()
    {
        await DisplayAlert(title: "OS not supported",
            message: "Your OS is not supported. Please upgrade to Windows 10 Fall Creators Update or newer.",
            cancel: "OK");
        Application.Current!.Quit();
    }

    private async void OnPageLoaded(object sender, EventArgs e)
    {
        _configurationManager = Handler!.MauiContext!.Services.GetRequiredService<IAppConfigurationManager>();
        _discordClient = (DiscordSocketClient)Handler.MauiContext.Services.GetRequiredService<IDiscordClient>();
        _logger = Handler.MauiContext.Services.GetRequiredService<ILogger<MainPage>>();

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                _logger.LogCritical(exception, message: null);
            }
        };

        var config = await _configurationManager.GetConfigurationAsync();

        var encryptionKey = await GetEncryptionKeyAsync(config);
        var localPath = GetLocalPath(config);

        if (config.Discord?.Enabled ?? false)
        {
            _storageProvider = Handler!.MauiContext!.Services.GetRequiredService<IDiscordStorageProvider>();
            var options = new DiscordStorageProviderOptions
            {
                ProviderId = DiscordStorageProvider.ProviderId,
                ProviderVersion = DiscordStorageProvider.ProviderVersion,
                LocalPath = localPath,
                BotToken = await GetBotTokenAsync(config),
                GuildId = await GetGuildIdAsync(config),
                EncryptionKey = encryptionKey,
                DbChannelName = GetDbChannel(config),
                DataChannelName = GetDataChannel(config),
                UseCompression = config.UseCompression
            };

            try
            {
                await _storageProvider.RegisterAsync(options);
            }
            catch (NotSupportedException)
            {
                await OnAppNotSupported();
                return;
            }
        }

        await _configurationManager.WriteConfigurationAsync();
        OnAppIsReady();
    }

    private async Task ValidateDiscordTokenAndConnectAsync(string discordBotToken)
    {
        var connectTask = new TaskCompletionSource();
        _discordClient.Ready += () =>
        {
            if (!connectTask.Task.IsCompleted)
            {
                connectTask.SetResult();
            }

            return Task.CompletedTask;
        };

        await _discordClient.LoginAsync(TokenType.Bot, discordBotToken, validateToken: true);
        await _discordClient.StartAsync();

        var task = await Task.WhenAny(connectTask.Task, Task.Delay(TimeSpan.FromSeconds(value: 30)));
        if (task != connectTask.Task)
        {
            connectTask.SetCanceled();
            await _discordClient.StopAsync();
            throw new Exception(message: "Discord bot connection timeout");
        }
    }

    private void OnQuitButtonClick(object sender, EventArgs e)
    {
        _storageProvider.Dispose();
        _ = Task.Factory.StartNew(async () =>
        {
            await Task.Delay(millisecondsDelay: 5000);
            Application.Current?.Quit();
        });
    }
}