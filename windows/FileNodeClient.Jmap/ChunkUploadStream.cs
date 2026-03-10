using System.Security.Cryptography;

namespace FileNodeClient.Jmap;

/// <summary>
/// Read-only stream wrapper that reads at most <paramref name="chunkSize"/> bytes from the
/// underlying stream, computing chunk and overall SHA1 hashes incrementally as data flows
/// through, and reporting upload progress as cumulative bytes. Avoids buffering the entire
/// chunk in memory. Throttles progress callbacks to at most once per 100ms.
/// </summary>
sealed class ChunkUploadStream : Stream
{
    private readonly Stream _inner;
    private readonly int _chunkSize;
    private int _bytesRead;
    private readonly IncrementalHash _chunkHash;
    private readonly IncrementalHash _overallHash;
    private readonly long _baseUploaded;
    private readonly long _totalSize;
    private readonly Action<long>? _onProgress;
    private readonly Action? _onBytesRead;
    private long _lastReportedTicks;
    private const long ThrottleIntervalTicks = 100 * TimeSpan.TicksPerMillisecond;

    public ChunkUploadStream(
        Stream inner, int chunkSize,
        IncrementalHash chunkHash, IncrementalHash overallHash,
        long baseUploaded, long totalSize,
        Action<long>? onProgress, Action? onBytesRead)
    {
        _inner = inner;
        _chunkSize = chunkSize;
        _chunkHash = chunkHash;
        _overallHash = overallHash;
        _baseUploaded = baseUploaded;
        _totalSize = totalSize;
        _onProgress = onProgress;
        _onBytesRead = onBytesRead;
    }

    public int TotalBytesRead => _bytesRead;

    public string GetChunkSha1Base64()
    {
        var hash = _chunkHash.GetHashAndReset();
        return Convert.ToBase64String(hash);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _chunkSize;
    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _chunkSize - _bytesRead;
        if (remaining <= 0) return 0;
        var toRead = Math.Min(count, remaining);
        var read = _inner.Read(buffer, offset, toRead);
        if (read > 0) ProcessBytes(buffer.AsSpan(offset, read));
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var remaining = _chunkSize - _bytesRead;
        if (remaining <= 0) return 0;
        var toRead = Math.Min(count, remaining);
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, toRead), ct);
        if (read > 0) ProcessBytes(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var remaining = _chunkSize - _bytesRead;
        if (remaining <= 0) return 0;
        var limited = buffer.Length > remaining ? buffer[..remaining] : buffer;
        var read = await _inner.ReadAsync(limited, ct);
        if (read > 0)
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(buffer[..read], out var segment))
            {
                ProcessBytes(segment.AsSpan());
            }
        }
        return read;
    }

    private void ProcessBytes(ReadOnlySpan<byte> data)
    {
        _chunkHash.AppendData(data);
        _overallHash.AppendData(data);
        _bytesRead += data.Length;
        _onBytesRead?.Invoke();

        if (_onProgress != null)
        {
            var totalBytes = _baseUploaded + _bytesRead;
            var nowTicks = DateTime.UtcNow.Ticks;
            var isLast = totalBytes >= _totalSize;
            if (nowTicks - _lastReportedTicks >= ThrottleIntervalTicks || isLast)
            {
                _lastReportedTicks = nowTicks;
                _onProgress(totalBytes);
            }
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
