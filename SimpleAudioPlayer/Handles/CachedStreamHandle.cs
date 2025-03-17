using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;

namespace SimpleAudioPlayer.Handles;

public class CachedStreamHandle : AudioCallbackHandlerBase
{
    #region 结构定义

    private struct DataChunk
    {
        public long StartOffset;
        public byte[] Buffer;
        public int Length;
    }

    #endregion

    #region 事件定义

    public event Action<long, long?>? ProgressChanged; // 当前进度，总大小（可能为null）
    public event Action<bool, Exception?>? DownloadCompleted;

    #endregion

    #region 字段

    private readonly ConcurrentQueue<DataChunk> _dataQueue = new();
    private readonly Task _downloadTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _dataSemaphore = new(0);
    private readonly object _syncRoot = new();

    private long _totalDownloaded;
    private long _currentPosition;
    private long? _totalSize;
    private bool _isDisposed;
    private bool _isCompleted;
    private Exception? _error;

    #endregion

    #region 构造函数
    public CachedStreamHandle(Stream stream, int bufferSize = 81920, long totalSize = -1)
    {
        _totalDownloaded = 0;
        _currentPosition = 0;
        _error = null;
        _downloadTask = RunDownloadTask(stream, bufferSize, totalSize);
    }

    #endregion

    #region 核心下载逻辑

    private async Task RunDownloadTask(Stream stream, int bufferSize, long totalSize)
    {
        try
        {
            // 获取总大小（可能不可用）
            _totalSize = totalSize != -1 ? totalSize : stream.CanSeek ? stream.Length : 0;

            var buffer = new byte[bufferSize];

            while (!_cts.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                if (bytesRead == 0) break;

                var chunk = new DataChunk
                {
                    StartOffset = Interlocked.Read(ref _totalDownloaded),
                    Buffer = new byte[bytesRead],
                    Length = bytesRead
                };

                Buffer.BlockCopy(buffer, 0, chunk.Buffer, 0, bytesRead);
                _dataQueue.Enqueue(chunk);

                Interlocked.Add(ref _totalDownloaded, bytesRead);
                _dataSemaphore.Release();

                // 报告进度
                ProgressChanged?.Invoke(_totalDownloaded, _totalSize);
            }

            CompleteDownload(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CompleteDownload(false, ex);
        }
        finally
        {
            _dataSemaphore.Release(); // 确保唤醒所有等待
        }
    }

    #endregion

    #region 公共方法

    public byte[] GetAllBytes()
    {
        if (!_isCompleted) throw new InvalidOperationException("Download not completed");
        if (_error != null) throw new AggregateException(_error);

        lock (_syncRoot)
        {
            var orderedChunks = _dataQueue
                .OrderBy(c => c.StartOffset)
                .ToList();

            var result = new byte[_totalDownloaded];
            long current = 0;

            foreach (var chunk in orderedChunks)
            {
                if (chunk.StartOffset > current)
                    throw new DataMisalignedException("Missing data chunks");

                Buffer.BlockCopy(chunk.Buffer, 0, result, (int)chunk.StartOffset, chunk.Length);
                current = chunk.StartOffset + chunk.Length;
            }

            return result;
        }
    }

    public void Cancel() => _cts.Cancel();

    #endregion

    #region 音频回调实现

    public override MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, ulong bytesToRead, out UIntPtr bytesRead)
    {
        bytesRead = UIntPtr.Zero;
        if (_isDisposed) return MaResult.MaError;

        int remaining = (int)bytesToRead;
        int totalCopied = 0;
        long currentPos = Interlocked.Read(ref _currentPosition);

        while (remaining > 0 && !_isDisposed)
        {
            if (!TryGetAvailableChunk(currentPos, out var chunk))
            {
                if (_isCompleted) break;
                _dataSemaphore.Wait(50, _cts.Token);
                continue;
            }

            int chunkOffset = (int)(currentPos - chunk.StartOffset);
            int available = chunk.Length - chunkOffset;
            int copySize = Math.Min(remaining, available);

            Marshal.Copy(chunk.Buffer, chunkOffset, pBuffer + totalCopied, copySize);

            totalCopied += copySize;
            remaining -= copySize;
            currentPos += copySize;
            Interlocked.Exchange(ref _currentPosition, currentPos);

            HandlePartialChunk(chunk, copySize);
        }

        bytesRead = (UIntPtr)totalCopied;
        return totalCopied > 0 ? MaResult.MaSuccess :
            _isCompleted ? MaResult.MaAtEnd : MaResult.MaBusy;
    }

    public override MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Interlocked.Read(ref _currentPosition) + offset,
            SeekOrigin.End => Interlocked.Read(ref _totalDownloaded) + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        const int timeoutMs = 10000;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            long downloaded = Interlocked.Read(ref _totalDownloaded);
            if (target <= downloaded)
            {
                Interlocked.Exchange(ref _currentPosition, target);
                return MaResult.MaSuccess;
            }

            if (_isCompleted || sw.ElapsedMilliseconds > timeoutMs)
                return MaResult.MaInvalidArgs;

            Thread.Sleep(CalculateWaitTime(target - downloaded));
        }
    }

    public override MaResult OnTell(IntPtr pDecoder, out UIntPtr pCursor)
    {
        pCursor = (UIntPtr)Interlocked.Read(ref _currentPosition);
        return MaResult.MaSuccess;
    }

    #endregion

    #region 私有方法

    private void CompleteDownload(bool success, Exception? error)
    {
        lock (_syncRoot)
        {
            _isCompleted = true;
            _error = error;
            DownloadCompleted?.Invoke(success, error);
        }
    }

    private bool TryGetAvailableChunk(long position, out DataChunk chunk)
    {
        foreach (var c in _dataQueue)
        {
            if (position >= c.StartOffset && position < c.StartOffset + c.Length)
            {
                chunk = c;
                return true;
            }
        }

        chunk = default;
        return false;
    }

    private void HandlePartialChunk(DataChunk original, int used)
    {
        if (used >= original.Length) return;

        var remaining = new DataChunk
        {
            StartOffset = original.StartOffset + used,
            Buffer = new byte[original.Length - used],
            Length = original.Length - used
        };

        Buffer.BlockCopy(original.Buffer, used, remaining.Buffer, 0, remaining.Length);
        _dataQueue.Enqueue(remaining);
    }

    private static int CalculateWaitTime(long remainingBytes)
    {
        return remainingBytes switch
        {
            > 10_000_000 => 1000, // 10MB+ 等待1秒
            > 1_000_000 => 500, // 1MB+ 等待500ms
            > 100_000 => 250, // 100KB+ 等待250ms
            _ => 50 // 默认50ms
        };
    }

    #endregion

    #region 资源清理

    public override void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        _downloadTask.ContinueWith(_ =>
        {
            _dataSemaphore.Dispose();
            _cts.Dispose();
        });

    }

    #endregion
}