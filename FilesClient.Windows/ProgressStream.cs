namespace FilesClient.Windows;

/// <summary>
/// Read-only stream wrapper that reports upload progress as a percentage.
/// Only fires the callback when the integer percentage changes.
/// </summary>
sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly long _totalLength;
    private long _bytesRead;
    private readonly Action<int> _onProgress;
    private int _lastReported = -1;

    public ProgressStream(Stream inner, long totalLength, Action<int> onProgress)
    {
        _inner = inner;
        _totalLength = totalLength;
        _onProgress = onProgress;
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

        if (_totalLength <= 0)
            return;

        _bytesRead += bytesRead;
        var percent = (int)(_bytesRead * 100 / _totalLength);
        if (percent != _lastReported)
        {
            _lastReported = percent;
            _onProgress(percent);
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
