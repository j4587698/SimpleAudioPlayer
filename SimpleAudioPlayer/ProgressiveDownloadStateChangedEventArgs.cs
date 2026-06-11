using SimpleAudioPlayer.Enums;

namespace SimpleAudioPlayer;

public sealed class ProgressiveDownloadStateChangedEventArgs(
    ProgressiveDownloadState state,
    Exception? error,
    string partialFilePath,
    string? finalFilePath)
{
    public ProgressiveDownloadState State { get; } = state;
    public Exception? Error { get; } = error;
    public string PartialFilePath { get; } = partialFilePath;
    public string? FinalFilePath { get; } = finalFilePath;
}
