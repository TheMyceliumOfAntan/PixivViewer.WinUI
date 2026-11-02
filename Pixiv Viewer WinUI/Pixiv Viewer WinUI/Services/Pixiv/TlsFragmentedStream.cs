using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pixiv_Viewer_WinUI.Services.Pixiv;

/// <summary>
/// 把 TLS ClientHello 拆成多个记录再发出，绕开只认 SNI 的阻断。
///
/// 做法参考 Pixeval / Pixeval.Network.Maho（GPL-3.0）的 Fragmentation 实现：
/// 在 SNI 主机名的开头和中间各切一刀，每个分片重写 TLS 记录头（版本位用 0x0309），
/// 分片之间留 100ms 间隔，让 DPI 拼不出完整域名；记录边界变化不影响握手本身。
///
/// 只处理第一条握手记录（ClientHello），之后的数据原样透传。
/// </summary>
internal sealed class TlsFragmentedStream : Stream
{
    private const byte HandshakeContentType = 0x16;
    private const int RecordHeaderLength = 5;
    private const int FragmentDelayMilliseconds = 100;

    private readonly Stream _inner;
    private readonly string _host;
    private readonly byte[] _hostBytes;

    private byte[] _buffer = new byte[8192];
    private int _buffered;
    private bool _passThrough;

    public TlsFragmentedStream(Stream inner, string host)
    {
        _inner = inner;
        _host = host.Trim();
        _hostBytes = Encoding.ASCII.GetBytes(_host);
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_passThrough)
        {
            await _inner.WriteAsync(buffer, cancellationToken);
            return;
        }

        await CaptureAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(ReadOnlySpan<byte> buffer)
        => WriteAsync(buffer.ToArray().AsMemory()).AsTask().GetAwaiter().GetResult();

    private async Task CaptureAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        EnsureCapacity(_buffered + buffer.Length);
        buffer.CopyTo(_buffer.AsMemory(_buffered));
        _buffered += buffer.Length;

        if (_buffered < RecordHeaderLength)
        {
            return;
        }

        if (_buffer[0] != HandshakeContentType)
        {
            await FlushBufferAsync(cancellationToken);
            return;
        }

        var recordLength = (_buffer[3] << 8) | _buffer[4];
        var total = RecordHeaderLength + recordLength;

        if (_buffered < total)
        {
            // 记录还没收全（SslStream 分多次 Write）：继续攒
            EnsureCapacity(total);
            return;
        }

        var payload = new byte[recordLength];
        Buffer.BlockCopy(_buffer, RecordHeaderLength, payload, 0, recordLength);

        await WriteFragmentsAsync(payload, cancellationToken);

        // 同一次 Write 里若还有后续记录，原样透传
        var remainder = _buffered - total;
        _passThrough = true;

        if (remainder > 0)
        {
            Buffer.BlockCopy(_buffer, total, _buffer, 0, remainder);
            _buffered = remainder;
            await _inner.WriteAsync(_buffer.AsMemory(0, remainder), cancellationToken);
            await _inner.FlushAsync(cancellationToken);
        }

        _buffered = 0;
    }

    private async Task WriteFragmentsAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var cuts = LocateCuts(payload);
        var header = new byte[RecordHeaderLength];
        header[0] = HandshakeContentType;
        header[1] = 0x03;
        header[2] = 0x09;

        var start = 0;

        for (var index = 0; index <= cuts.Count; index++)
        {
            if (index > 0)
            {
                await Task.Delay(FragmentDelayMilliseconds, cancellationToken);
            }

            var end = index < cuts.Count ? cuts[index] : payload.Length;
            if (end <= start || end > payload.Length)
            {
                continue;
            }

            var length = end - start;
            header[3] = (byte)(length >> 8);
            header[4] = (byte)length;

            await _inner.WriteAsync(header.AsMemory(), cancellationToken);
            await _inner.WriteAsync(payload.AsMemory(start, length), cancellationToken);
            await _inner.FlushAsync(cancellationToken);

            start = end;
        }
    }

    /// <summary>在 SNI 主机名的开头和中间切两刀；找不到主机名时退化为固定位置。</summary>
    private System.Collections.Generic.List<int> LocateCuts(byte[] payload)
    {
        var cuts = new System.Collections.Generic.List<int>(2);

        var index = _hostBytes.Length > 0 ? payload.AsSpan().IndexOf(_hostBytes) : -1;

        if (index > 0)
        {
            var middle = index + Math.Max(1, _hostBytes.Length / 2);
            AddCut(cuts, index, payload.Length);
            AddCut(cuts, middle, payload.Length);
        }

        if (cuts.Count == 0)
        {
            AddCut(cuts, 120, payload.Length);
            AddCut(cuts, 260, payload.Length);
        }

        return cuts;
    }

    private static void AddCut(System.Collections.Generic.List<int> cuts, int position, int payloadLength)
    {
        if (position <= 0 || position >= payloadLength)
        {
            return;
        }

        if (cuts.Count > 0 && cuts[^1] >= position)
        {
            return;
        }

        cuts.Add(position);
    }

    private async Task FlushBufferAsync(CancellationToken cancellationToken)
    {
        _passThrough = true;

        if (_buffered > 0)
        {
            await _inner.WriteAsync(_buffer.AsMemory(0, _buffered), cancellationToken);
            await _inner.FlushAsync(cancellationToken);
        }

        _buffered = 0;
    }

    private void EnsureCapacity(int size)
    {
        if (size <= _buffer.Length)
        {
            return;
        }

        Array.Resize(ref _buffer, Math.Max(size, _buffer.Length * 2));
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
