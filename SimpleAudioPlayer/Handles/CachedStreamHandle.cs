using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;

namespace SimpleAudioPlayer.Handles
{
    public class CachedStreamHandle : AudioCallbackHandlerBase
    {
        #region 事件定义
        public event Action<long, long?>? ProgressChanged;
        public event Action<bool, Exception?>? DownloadCompleted;
        #endregion

        #region 字段
        private readonly Task _downloadTask;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _dataAvailable = new(0);
        private readonly object _bufferLock = new();

        private byte[]? _completeBuffer;
        private long _bufferCapacity;
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
            _totalSize = totalSize > 0 ? totalSize : stream.CanSeek ? stream.Length : null;
            InitializeBuffer();
            _downloadTask = RunDownloadTask(stream, bufferSize);
        }
        #endregion

        #region 核心下载逻辑
        private void InitializeBuffer()
        {
            lock (_bufferLock)
            {
                // 如果知道总大小，预分配精确大小的缓冲区
                if (_totalSize.HasValue)
                {
                    _completeBuffer = new byte[_totalSize.Value];
                    _bufferCapacity = _totalSize.Value;
                }
                else
                {
                    // 初始缓冲区大小
                    _completeBuffer = new byte[1024 * 1024]; // 1MB初始容量
                    _bufferCapacity = _completeBuffer.Length;
                }
            }
        }

        private async Task RunDownloadTask(Stream stream, int readBufferSize)
        {
            try
            {
                int totalRead = 0;
                var readBuffer = new byte[readBufferSize];

                while (!_cts.IsCancellationRequested)
                {
                    int bytesRead = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, _cts.Token);
                    if (bytesRead == 0) break;

                    lock (_bufferLock)
                    {
                        // 检查并扩展缓冲区（如果不知道总大小）
                        if (!_totalSize.HasValue && totalRead + bytesRead > _bufferCapacity)
                        {
                            long newCapacity = Math.Max(_bufferCapacity * 2, totalRead + bytesRead);
                            Array.Resize(ref _completeBuffer, (int)newCapacity);
                            _bufferCapacity = newCapacity;
                        }

                        // 复制数据到主缓冲区
                        Buffer.BlockCopy(readBuffer, 0, _completeBuffer!, totalRead, bytesRead);
                        totalRead += bytesRead;
                        _totalDownloaded = totalRead;
                    }

                    _dataAvailable.Release();
                    ProgressChanged?.Invoke(totalRead, _totalSize);
                }

                // 如果不知道总大小，最终调整缓冲区大小
                if (!_totalSize.HasValue && totalRead < _bufferCapacity)
                {
                    lock (_bufferLock)
                    {
                        Array.Resize(ref _completeBuffer, totalRead);
                        _bufferCapacity = totalRead;
                        _totalSize = totalRead;
                    }
                }

                CompleteDownload(true, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CompleteDownload(false, ex);
            }
            finally
            {
                _dataAvailable.Release(); // 确保唤醒等待的读取操作
            }
        }
        #endregion

        #region 公共方法
        public byte[] GetAllBytes()
        {
            if (!_isCompleted) throw new InvalidOperationException("Download not completed");
            if (_error != null) throw new AggregateException(_error);

            lock (_bufferLock)
            {
                var result = new byte[_totalDownloaded];
                Buffer.BlockCopy(_completeBuffer!, 0, result, 0, (int)_totalDownloaded);
                return result;
            }
        }

        public void Cancel() => _cts.Cancel();
        #endregion

        #region 音频回调实现
        public override MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, nuint bytesToRead, out nuint bytesRead)
        {
            bytesRead = UIntPtr.Zero;
            if (_isDisposed) return MaResult.MaError;

            // 等待数据可用或下载完成
            while (!_isCompleted && _currentPosition >= _totalDownloaded)
            {
                if (!_dataAvailable.Wait(50, _cts.Token))
                {
                    return _cts.IsCancellationRequested ? MaResult.MaError : MaResult.MaBusy;
                }
            }

            lock (_bufferLock)
            {
                long available = _totalDownloaded - _currentPosition;
                if (available <= 0)
                {
                    return _isCompleted ? MaResult.MaAtEnd : MaResult.MaBusy;
                }

                int bytesToCopy = (int)Math.Min((long)bytesToRead, available);
                Marshal.Copy(_completeBuffer!, (int)_currentPosition, pBuffer, bytesToCopy);
                
                _currentPosition += bytesToCopy;
                bytesRead = (nuint)bytesToCopy;
                
                return MaResult.MaSuccess;
            }
        }

        public override MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Interlocked.Read(ref _currentPosition) + offset,
                SeekOrigin.End => (_totalSize ?? _totalDownloaded) + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            // 等待数据下载到目标位置（如果必要）
            if (target > _totalDownloaded)
            {
                if (_isCompleted) return MaResult.MaInvalidArgs;
                
                const int timeoutMs = 10000;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                
                while (target > _totalDownloaded)
                {
                    if (_isCompleted || sw.ElapsedMilliseconds > timeoutMs)
                        return MaResult.MaInvalidArgs;
                    
                    Thread.Sleep(50);
                }
            }

            Interlocked.Exchange(ref _currentPosition, Math.Min(target, _totalDownloaded));
            return MaResult.MaSuccess;
        }

        public override MaResult OnTell(IntPtr pDecoder, out long pCursor)
        {
            pCursor = Interlocked.Read(ref _currentPosition);
            return MaResult.MaSuccess;
        }
        #endregion

        #region 私有方法
        private void CompleteDownload(bool success, Exception? error)
        {
            lock (_bufferLock)
            {
                _isCompleted = true;
                _error = error;
                DownloadCompleted?.Invoke(success, error);
            }
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
                _dataAvailable.Dispose();
                _cts.Dispose();
                
                lock (_bufferLock)
                {
                    _completeBuffer = null; // 释放大内存块
                }
            });
        }
        #endregion
    }
}