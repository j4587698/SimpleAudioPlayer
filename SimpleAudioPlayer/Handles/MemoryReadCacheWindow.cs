using System;
using System.IO;

namespace SimpleAudioPlayer.Handles;

/// <summary>
/// 为磁盘缓存音频流提供内存滑动窗口读取缓冲（RAM Read Cache Window）。
/// 避免音频解码线程在每次高频微小读取（例如每几毫秒读取 4KB）时反复执行物理磁盘 I/O，
/// 大幅降低磁盘高占用或 I/O 抖动时的音频欠载（Buffer Underrun / 颤音）风险。
/// </summary>
internal sealed class MemoryReadCacheWindow
{
    public const int DefaultCapacity = 2 * 1024 * 1024; // 2MB 默认内存滑动窗口

    private readonly byte[] _buffer;
    private long _startOffset = -1;
    private int _validLength = 0;

    public MemoryReadCacheWindow(int capacity = DefaultCapacity)
    {
        _buffer = new byte[capacity > 0 ? capacity : DefaultCapacity];
    }

    public long StartOffset => _startOffset;
    public int ValidLength => _validLength;
    public int Capacity => _buffer.Length;

    /// <summary>
    /// 尝试直接从内存滑动窗口中读取数据。
    /// </summary>
    public bool TryRead(long position, Span<byte> destination, out int bytesRead)
    {
        bytesRead = 0;
        if (_startOffset < 0 || _validLength <= 0)
        {
            return false;
        }

        if (position < _startOffset || position >= _startOffset + _validLength)
        {
            return false;
        }

        int offsetInCache = (int)(position - _startOffset);
        int available = _validLength - offsetInCache;
        if (available <= 0)
        {
            return false;
        }

        bytesRead = Math.Min(destination.Length, available);
        _buffer.AsSpan(offsetInCache, bytesRead).CopyTo(destination);
        return true;
    }

    /// <summary>
    /// 当下载器向磁盘写入新数据块时，同步更新或扩展内存滑动窗口。
    /// </summary>
    public void OnWrite(long position, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        // 首次写入或缓存为空时：以当前写入位置初始化窗口
        if (_startOffset < 0 || _validLength == 0)
        {
            _startOffset = position;
            int copyCount = Math.Min(data.Length, _buffer.Length);
            data.Slice(0, copyCount).CopyTo(_buffer);
            _validLength = copyCount;
            return;
        }

        // 写入位置在当前窗口范围内或紧随其后（允许向后扩展，最高到 Capacity）
        if (position >= _startOffset && position <= _startOffset + _buffer.Length)
        {
            int offset = (int)(position - _startOffset);
            int availableCapacity = _buffer.Length - offset;
            if (availableCapacity > 0)
            {
                int copyCount = Math.Min(data.Length, availableCapacity);
                data.Slice(0, copyCount).CopyTo(_buffer.AsSpan(offset, copyCount));
                _validLength = Math.Max(_validLength, offset + copyCount);
            }
            return;
        }

        // 写入位置在窗口之前且与窗口重叠
        if (position < _startOffset && position + data.Length > _startOffset)
        {
            long overlapStart = _startOffset - position;
            int copyCount = (int)Math.Min(data.Length - overlapStart, _buffer.Length);
            if (copyCount > 0)
            {
                data.Slice((int)overlapStart, copyCount).CopyTo(_buffer.AsSpan(0, copyCount));
                _validLength = Math.Max(_validLength, copyCount);
            }
        }
    }

    /// <summary>
    /// 当内存窗口未命中时，从磁盘一次性批量预读最多 Capacity 的连续数据填充内存窗口。
    /// </summary>
    public void FillFromDisk(Stream fileStream, long position, CachedByteRangeSet cachedRanges)
    {
        long availableOnDisk = cachedRanges.GetContiguousAvailable(position, _buffer.Length);
        if (availableOnDisk <= 0)
        {
            return;
        }

        int bytesToRead = (int)Math.Min(_buffer.Length, availableOnDisk);
        try
        {
            fileStream.Position = position;
            int totalRead = 0;
            while (totalRead < bytesToRead)
            {
                int read = fileStream.Read(_buffer, totalRead, bytesToRead - totalRead);
                if (read <= 0)
                {
                    break;
                }
                totalRead += read;
            }

            if (totalRead > 0)
            {
                _startOffset = position;
                _validLength = totalRead;
            }
        }
        catch
        {
            // 容错：如果磁盘读取发生瞬时异常，保留当前状态，让上层去处理
        }
    }

    /// <summary>
    /// 重置/失效内存缓存窗口。
    /// </summary>
    public void Reset()
    {
        _startOffset = -1;
        _validLength = 0;
    }
}
