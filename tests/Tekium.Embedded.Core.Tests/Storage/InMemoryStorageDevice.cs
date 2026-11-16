// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Storage;

/// <summary>
/// Doble de prueba del dispositivo: memoria en vez de archivo, con inyección de fallos de escritura.
/// Los datos sobreviven a <see cref="DisposeAsync"/> para poder "reabrir" la base en la misma prueba.
/// </summary>
internal sealed class InMemoryStorageDevice : IStorageDevice
{
    private byte[] _data = [];
    private long _length;

    public long Length => _length;

    public int WriteCount { get; private set; }

    public int FlushCount { get; private set; }

    /// <summary>Gets or sets the ordinal of the write (1-based, counted from creation) that throws.</summary>
    public int? FailOnWriteNumber { get; set; }

    public Span<byte> RawBytes => _data.AsSpan(0, (int)_length);

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (offset + destination.Length > _length)
        {
            throw new EndOfStreamException();
        }

        _data.AsSpan((int)offset, destination.Length).CopyTo(destination.Span);
        return default;
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        WriteCount++;
        if (WriteCount == FailOnWriteNumber)
        {
            throw new IOException("Injected write failure.");
        }

        var end = offset + source.Length;
        EnsureCapacity(end);
        source.Span.CopyTo(_data.AsSpan((int)offset));
        _length = Math.Max(_length, end);
        return default;
    }

    public void SetLength(long length)
    {
        EnsureCapacity(length);
        if (length < _length)
        {
            _data.AsSpan((int)length, (int)(_length - length)).Clear();
        }

        _length = length;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        FlushCount++;
        return default;
    }

    public ValueTask DisposeAsync() => default;

    private void EnsureCapacity(long required)
    {
        if (required > _data.Length)
        {
            Array.Resize(ref _data, (int)Math.Max(required, _data.Length * 2L));
        }
    }
}
