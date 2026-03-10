namespace FileNodeClient.Windows;

/// <summary>
/// Read-only stream wrapper that reports upload progress as cumulative bytes read.
/// Throttles progress callbacks to at most once per 100ms, always fires on final read.
/// </summary>
sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly long _totalLength;
    private readonly Action<long> _onProgress;
    private readonly Action? _onBytesRead;
    private long _bytesRead;
    private long _lastReportedTicks;
    private const long ThrottleIntervalTicks = 100 * TimeSpan.TicksPerMillisecond;

    public ProgressStream(Stream inner, long totalLength, Action<long> onProgress, Action? onBytesRead = null)
    {
        _inner = inner;
        _totalLength = totalLength;
        _onProgress = onProgress;
        _onBytesRead = onBytesRead;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _inner.Read(buffer, offset, count);
        ReportProgress(bytesRead);
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        ReportProgress(bytesRead);
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _inner.ReadAsync(buffer, cancellationToken);
        ReportProgress(bytesRead);
        return bytesRead;
    }

    private void ReportProgress(int bytesRead)
    {
        if (bytesRead <= 0)
            return;

        _onBytesRead?.Invoke();

        _bytesRead += bytesRead;

        var nowTicks = DateTime.UtcNow.Ticks;
        if (nowTicks - _lastReportedTicks >= ThrottleIntervalTicks || _bytesRead >= _totalLength)
        {
            _lastReportedTicks = nowTicks;
            _onProgress(_bytesRead);
        }
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}
