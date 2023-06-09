﻿using DiscordFS.Platforms.Windows.Storage;

namespace DiscordFS.Storage.FileSystem.Results;

public class WriteFileCloseResult : FileOperationResult
{
    public WriteFileCloseResult() { }

    public WriteFileCloseResult(CloudFileFetchErrorCode error) : base(error) { }

    public WriteFileCloseResult(Exception ex) : base(ex) { }

    public FilePlaceholder Placeholder { get; set; }
}