namespace SimpleAudioPlayer.Handles;

internal readonly record struct CachedByteRange(long Start, long Length)
{
    public long EndExclusive => Start + Length;
}
