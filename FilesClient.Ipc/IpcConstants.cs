namespace FilesClient.Ipc;

public static class IpcConstants
{
    public const string PipeName = "FastmailFiles";
    public const int BufferSize = 65536;
    public const int ReconnectDelayMs = 5000;
}
