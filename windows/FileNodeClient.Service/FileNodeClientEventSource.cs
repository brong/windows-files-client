using System.Diagnostics.Tracing;

namespace FileNodeClient.Service;

/// <summary>
/// ETW event source for FileNodeClient Service. Events appear in Event Viewer under
/// "Applications and Services Logs > Fastmail-FileNodeClient-Service > Operational"
/// (channel registered via desktop8:EventTracing in AppxManifest.xml).
/// </summary>
[EventSource(Name = "Fastmail-FileNodeClient-Service")]
sealed class FileNodeClientEventSource : EventSource
{
    public static readonly FileNodeClientEventSource Instance = new();

    [Event(1, Level = EventLevel.Informational, Channel = EventChannel.Operational,
           Message = "{0}")]
    public void InfoMessage(string message) => WriteEvent(1, message);

    [Event(2, Level = EventLevel.Warning, Channel = EventChannel.Operational,
           Message = "{0}")]
    public void WarningMessage(string message) => WriteEvent(2, message);

    [Event(3, Level = EventLevel.Error, Channel = EventChannel.Operational,
           Message = "{0}")]
    public void ErrorMessage(string message) => WriteEvent(3, message);

    [Event(4, Level = EventLevel.Verbose, Channel = EventChannel.Operational,
           Message = "{0}")]
    public void DebugMessage(string message) => WriteEvent(4, message);
}
