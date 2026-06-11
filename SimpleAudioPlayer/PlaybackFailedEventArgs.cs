using SimpleAudioPlayer.Enums;

namespace SimpleAudioPlayer;

public sealed class PlaybackFailedEventArgs : EventArgs
{
    public PlaybackFailedEventArgs(MaResult result, Exception? exception)
    {
        Result = result;
        Exception = exception;
    }

    public MaResult Result { get; }

    public Exception? Exception { get; }
}
