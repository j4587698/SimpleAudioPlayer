namespace SimpleAudioPlayer.Handles;

internal sealed class CachedByteRangeSet
{
    private readonly List<CachedByteRange> _ranges = [];

    public IReadOnlyList<CachedByteRange> Ranges => _ranges;
    public long TotalBytes { get; private set; }

    public void Replace(IEnumerable<CachedByteRange> ranges)
    {
        _ranges.Clear();
        TotalBytes = 0;

        foreach (var range in ranges.OrderBy(static r => r.Start))
        {
            Add(range.Start, range.Length);
        }
    }

    public void Add(long start, long length)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (length <= 0)
        {
            return;
        }

        var newStart = start;
        var newEnd = checked(start + length);
        var insertAt = 0;

        while (insertAt < _ranges.Count && _ranges[insertAt].EndExclusive < newStart)
        {
            insertAt++;
        }

        while (insertAt < _ranges.Count && _ranges[insertAt].Start <= newEnd)
        {
            var existing = _ranges[insertAt];
            newStart = Math.Min(newStart, existing.Start);
            newEnd = Math.Max(newEnd, existing.EndExclusive);
            TotalBytes -= existing.Length;
            _ranges.RemoveAt(insertAt);
        }

        var merged = new CachedByteRange(newStart, newEnd - newStart);
        _ranges.Insert(insertAt, merged);
        TotalBytes += merged.Length;
    }

    public long GetContiguousAvailable(long position, long maxLength)
    {
        if (maxLength <= 0)
        {
            return 0;
        }

        foreach (var range in _ranges)
        {
            if (position < range.Start)
            {
                return 0;
            }

            if (position >= range.Start && position < range.EndExclusive)
            {
                return Math.Min(maxLength, range.EndExclusive - position);
            }
        }

        return 0;
    }

    public bool Covers(long start, long length)
    {
        return GetContiguousAvailable(start, length) >= length;
    }

    public bool CoversCompleteFile(long totalBytes)
    {
        return totalBytes >= 0 && _ranges.Count == 1 && _ranges[0].Start == 0 && _ranges[0].EndExclusive >= totalBytes;
    }

    public long? FindFirstMissing(long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return null;
        }

        var cursor = 0L;
        foreach (var range in _ranges)
        {
            if (range.Start > cursor)
            {
                return cursor;
            }

            if (range.EndExclusive > cursor)
            {
                cursor = range.EndExclusive;
            }

            if (cursor >= totalBytes)
            {
                return null;
            }
        }

        return cursor < totalBytes ? cursor : null;
    }

    public long? FindNextRangeStartAfter(long position)
    {
        foreach (var range in _ranges)
        {
            if (range.Start > position)
            {
                return range.Start;
            }
        }

        return null;
    }
}
