using System.ComponentModel;
using DiscordFS.Platforms.Windows.Storage;

namespace DiscordFS.Storage.Files.Results;

public class DeleteFileResult : FileOperationResult
{
    public DeleteFileResult() { }

    public DeleteFileResult(CloudFileFetchErrorCode error) : base(error) { }
}

public class MoveFileResult : FileOperationResult
{
    public MoveFileResult() { }

    public MoveFileResult(CloudFileFetchErrorCode error) : base(error) { }
}

public class CreateFileResult : FileOperationResult
{
    public FilePlaceholder FilePlaceholder;

    public CreateFileResult() { }

    public CreateFileResult(CloudFileFetchErrorCode error) : base(error) { }
}

public class FileOperationResult<T> : FileOperationResult
{
    public T Data;

    public FileOperationResult()
    {
        Succeeded = true;
        Status = CloudFilterNTStatus.STATUS_SUCCESS;
        Message = Status.ToString();
    }

    public FileOperationResult(Exception ex)
    {
        SetException(ex);
    }

    public FileOperationResult(CloudFileFetchErrorCode ex)
    {
        SetError(ex);
    }

    public FileOperationResult(CloudFilterNTStatus status)
    {
        Succeeded = status == CloudFilterNTStatus.STATUS_SUCCESS;
        Status = status;
        Message = Status.ToString();
    }

    public FileOperationResult(int ntStatus)
    {
        Status = (CloudFilterNTStatus)ntStatus;
        Succeeded = ntStatus == 0;
        Message = Status.ToString();
    }

    public FileOperationResult(CloudFilterNTStatus status, string message)
    {
        Succeeded = status == CloudFilterNTStatus.STATUS_SUCCESS;
        Status = status;
        Message = message;
    }
}

public class GetNextFileResult : FileOperationResult
{
    public FilePlaceholder FilePlaceholder;

    public GetNextFileResult() { }

    public GetNextFileResult(CloudFilterNTStatus status)
    {
        Status = status;
    }
}

public enum CloudFileFetchErrorCode
{
    Offline = 1,
    FileOrDirectoryNotFound = 2,
    AccessDenied = 3
}

public class FileOperationResult
{
    public string Message { get; set; }

    public CloudFilterNTStatus Status
    {
        get { return _status; }
        set
        {
            _status = value;
            Succeeded = _status == CloudFilterNTStatus.STATUS_SUCCESS;
        }
    }

    public bool Succeeded { get; set; }

    private CloudFilterNTStatus _status;

    public FileOperationResult()
    {
        Succeeded = true;
        Status = CloudFilterNTStatus.STATUS_SUCCESS;
        Message = Status.ToString();
    }

    public FileOperationResult(bool succeeded)
    {
        Succeeded = succeeded;
        Status = succeeded
            ? CloudFilterNTStatus.STATUS_SUCCESS
            : CloudFilterNTStatus.STATUS_UNSUCCESSFUL;
        Message = Status.ToString();
    }

    public FileOperationResult(Exception ex)
    {
        SetException(ex);
    }

    public FileOperationResult(CloudFileFetchErrorCode error)
    {
        SetError(error);
    }

    public FileOperationResult(CloudFilterNTStatus status)
    {
        Succeeded = status == CloudFilterNTStatus.STATUS_SUCCESS;
        Status = status;
        Message = Status.ToString();
    }

    public FileOperationResult(int ntStatus)
    {
        Status = (CloudFilterNTStatus)ntStatus;
        Succeeded = ntStatus == 0;
        Message = Status.ToString();
    }

    public FileOperationResult(CloudFilterNTStatus status, string message)
    {
        Succeeded = status == CloudFilterNTStatus.STATUS_SUCCESS;
        Status = status;
        Message = message;
    }

    public static implicit operator bool(FileOperationResult instance)
    {
        return instance.Succeeded;
    }

    public void SetError(CloudFileFetchErrorCode error)
    {
        Succeeded = false;
        Message = error.ToString();

        Status = error switch
        {
            CloudFileFetchErrorCode.Offline => CloudFilterNTStatus.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE,
            CloudFileFetchErrorCode.FileOrDirectoryNotFound => CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE,
            CloudFileFetchErrorCode.AccessDenied => CloudFilterNTStatus.STATUS_CLOUD_FILE_ACCESS_DENIED,
            _ => CloudFilterNTStatus.STATUS_CLOUD_FILE_UNSUCCESSFUL
        };
    }

    public void SetException(Exception ex)
    {
        Succeeded = false;
        Message = ex.ToString();

        Status = ex switch
        {
            FileNotFoundException => CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE,
            DirectoryNotFoundException => CloudFilterNTStatus.STATUS_NOT_A_CLOUD_FILE,
            UnauthorizedAccessException => CloudFilterNTStatus.STATUS_CLOUD_FILE_ACCESS_DENIED,
            IOException => CloudFilterNTStatus.STATUS_CLOUD_FILE_IN_USE,
            NotSupportedException => CloudFilterNTStatus.STATUS_CLOUD_FILE_NOT_SUPPORTED,
            InvalidOperationException => CloudFilterNTStatus.STATUS_CLOUD_FILE_INVALID_REQUEST,
            OperationCanceledException => CloudFilterNTStatus.STATUS_CLOUD_FILE_REQUEST_CANCELED,
            _ => CloudFilterNTStatus.STATUS_CLOUD_FILE_UNSUCCESSFUL
        };
    }

    public void ThrowOnFailure()
    {
        if (!Succeeded)
        {
            throw new Win32Exception((int)Status, Message);
        }
    }
}