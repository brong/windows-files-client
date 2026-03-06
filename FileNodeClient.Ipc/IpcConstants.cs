namespace FileNodeClient.Ipc;

public static class IpcConstants
{
    public const string PipeName = "FileNodeClient";
    public const int BufferSize = 65536;
    public const int ReconnectDelayMs = 5000;
}
