namespace DiscordFS.Storage.Files;

[Flags]
public enum UploadMode : short
{
    FullFile = 0,
    Resume = 1,
    PartialUpdate = 2
}