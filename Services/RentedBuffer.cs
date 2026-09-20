using System.Buffers;

namespace MiniOcr.Services;

/// <summary>
/// ArrayPool-backed buffer with an exact usable length. Dispose returns the array to the pool.
/// </summary>
public sealed class RentedBuffer : IDisposable
{
    private byte[]? _buffer;
    private readonly ArrayPool<byte> _pool;

    public RentedBuffer(byte[] buffer, int length, ArrayPool<byte> pool)
    {
        if ((uint)length > (uint)buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        _buffer = buffer;
        Length = length;
        _pool = pool;
    }

    public int Length { get; private set; }

    public Memory<byte> Memory =>
        _buffer is null ? throw new ObjectDisposedException(nameof(RentedBuffer)) : _buffer.AsMemory(0, Length);

    public Span<byte> Span =>
        _buffer is null ? throw new ObjectDisposedException(nameof(RentedBuffer)) : _buffer.AsSpan(0, Length);

    public byte[] DangerousGetArray() =>
        _buffer ?? throw new ObjectDisposedException(nameof(RentedBuffer));

    public void Dispose()
    {
        byte[]? buf = Interlocked.Exchange(ref _buffer, null);
        if (buf is not null)
            _pool.Return(buf);
    }
}
