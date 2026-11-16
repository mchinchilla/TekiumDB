// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Storage;

/// <summary>
/// Envuelve un dispositivo real del motor (archivo o memoria) para las pruebas: cuenta escrituras y
/// flushes, inyecta fallos de escritura, permite dañar bytes, y se puede volver a usar después de
/// <see cref="DisposeAsync"/> para "reabrir" la misma base. También simula un corte de energía:
/// deja de funcionar en una operación elegida y reconstruye lo que pudo haber quedado en el disco.
/// </summary>
internal sealed class TestDevice : IStorageDevice
{
    private readonly Func<IStorageDevice> _open;
    private readonly bool _closeOnDispose;
    private readonly bool _flushToDisk;
    private const int SectorSize = 512;

    private readonly List<(long Offset, byte[] Before, byte[] After)> _unflushed = [];
    private IStorageDevice? _inner;

    private TestDevice(Func<IStorageDevice> open, bool closeOnDispose, bool flushToDisk, string? path)
    {
        _open = open;
        _closeOnDispose = closeOnDispose;
        _flushToDisk = flushToDisk;
        FilePath = path;
    }

    /// <summary>Gets the backing file, or <see langword="null"/> for memory.</summary>
    public string? FilePath { get; }

    public long Length => Inner.Length;

    public int WriteCount { get; private set; }

    public int FlushCount { get; private set; }

    public int ReadCount { get; private set; }

    /// <summary>Si se fija, la lectura con ese número de orden falla sin leer nada.</summary>
    public int? FailOnReadNumber { get; set; }

    /// <summary>Gets or sets the ordinal of the write (1-based, counted from creation) that throws.</summary>
    public int? FailOnWriteNumber { get; set; }

    /// <summary>Gets or sets the ordinal of the flush (1-based) that throws. What it was flushing is left in doubt.</summary>
    public int? FailOnFlushNumber { get; set; }

    /// <summary>Gets or sets a value indicating whether growing the storage fails, as on a full disk.</summary>
    public bool DiskFull { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether writes are remembered until the next flush, which is what
    /// <see cref="PowerLossImageAsync"/> needs to know what a power loss could undo.
    /// </summary>
    public bool TrackUnflushedWrites { get; set; }

    /// <summary>Gets the number of operations that change the storage so far: writes, flushes and length changes.</summary>
    public int OperationCount { get; private set; }

    /// <summary>Gets or sets the ordinal of the operation (1-based) at which the power goes out, before it does anything.</summary>
    public int? PowerLossAtOperation { get; set; }

    /// <summary>Gets a value indicating whether the power went out: from then on every operation fails.</summary>
    public bool PowerLost { get; private set; }

    /// <summary>Gets or sets an observer of every write that reaches the storage: offset and bytes.</summary>
    public Action<long, ReadOnlyMemory<byte>>? OnWrite { get; set; }

    private IStorageDevice Inner => _inner ??= _open();

    /// <summary>Un dispositivo en memoria del motor. Los datos viven mientras viva este objeto.</summary>
    public static TestDevice Memory()
    {
        var memory = new MemoryStorageDevice();
        return new TestDevice(() => memory, closeOnDispose: false, flushToDisk: true, path: null);
    }

    /// <summary>Un archivo temporal. Cada reapertura abre un handle nuevo sobre el mismo archivo.</summary>
    public static TestDevice File(bool flushToDisk = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tekium-{Guid.NewGuid():N}.tkdb");
        return new TestDevice(
            () => FileStorageDevice.Open(path, FileMode.OpenOrCreate), closeOnDispose: true, flushToDisk, path);
    }

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (PowerLost)
        {
            return ValueTask.FromException(new IOException("Simulated power loss."));
        }

        ReadCount++;
        return ReadCount == FailOnReadNumber
            ? ValueTask.FromException(new IOException("Injected read failure."))
            : Inner.ReadAsync(offset, destination, cancellationToken);
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        if (LosesPower())
        {
            return ValueTask.FromException(new IOException("Simulated power loss."));
        }

        WriteCount++;
        if (WriteCount == FailOnWriteNumber)
        {
            return ValueTask.FromException(new IOException("Injected write failure."));
        }

        OnWrite?.Invoke(offset, source);
        return TrackUnflushedWrites
            ? TrackedWriteAsync(offset, source, cancellationToken)
            : Inner.WriteAsync(offset, source, cancellationToken);
    }

    public void SetLength(long length)
    {
        if (LosesPower())
        {
            throw new IOException("Simulated power loss.");
        }

        if (DiskFull && length > Inner.Length)
        {
            throw new IOException("Injected failure: no space left on device.");
        }

        Inner.SetLength(length);
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (LosesPower())
        {
            return ValueTask.FromException(new IOException("Simulated power loss."));
        }

        FlushCount++;
        if (FlushCount == FailOnFlushNumber)
        {
            return ValueTask.FromException(new IOException("Injected flush failure."));
        }

        _unflushed.Clear();
        return _flushToDisk ? Inner.FlushAsync(cancellationToken) : default;
    }

    /// <summary>
    /// Una copia de lo que pudo quedar en el disco si la energía se fuera ahora: todo lo que ya pasó por
    /// un flush, y de cada escritura posterior, lo que <paramref name="survives"/> decida sector por sector
    /// (recibe el índice de la escritura y el del sector). Un sector queda entero, viejo o nuevo.
    /// </summary>
    public async ValueTask<TestDevice> PowerLossImageAsync(Func<int, long, bool> survives)
    {
        var bytes = await ReadAllAsync();
        for (var i = _unflushed.Count - 1; i >= 0; i--)
        {
            _unflushed[i].Before.CopyTo(bytes.AsSpan((int)_unflushed[i].Offset));
        }

        for (var i = 0; i < _unflushed.Count; i++)
        {
            var (offset, _, after) = _unflushed[i];
            for (var start = 0; start < after.Length;)
            {
                var sector = (offset + start) / SectorSize;
                var count = (int)Math.Min(after.Length - start, ((sector + 1) * SectorSize) - (offset + start));
                if (survives(i, sector))
                {
                    after.AsSpan(start, count).CopyTo(bytes.AsSpan((int)offset + start));
                }

                start += count;
            }
        }

        var image = Memory();
        await image.Inner.WriteAsync(0, bytes, CancellationToken.None);
        return image;
    }

    /// <summary>Invierte bits de un byte directamente en el almacenamiento, como lo haría un disco que falla.</summary>
    public async ValueTask CorruptAsync(long offset, byte mask = 0xFF)
    {
        var one = new byte[1];
        await Inner.ReadAsync(offset, one, CancellationToken.None);
        one[0] ^= mask;
        await Inner.WriteAsync(offset, one, CancellationToken.None);
    }

    /// <summary>Lee todo el contenido tal como está almacenado.</summary>
    public async ValueTask<byte[]> ReadAllAsync()
    {
        var bytes = new byte[Inner.Length];
        await Inner.ReadAsync(0, bytes, CancellationToken.None);
        return bytes;
    }

    /// <summary>Una copia independiente en memoria: lo que pasa cuando alguien copia el archivo.</summary>
    public async ValueTask<TestDevice> CloneAsync()
    {
        var clone = Memory();
        await clone.Inner.WriteAsync(0, await ReadAllAsync(), CancellationToken.None);
        return clone;
    }

    /// <summary>Sobrescribe bytes directamente en el almacenamiento, sin pasar por el motor.</summary>
    public ValueTask OverwriteAsync(long offset, ReadOnlyMemory<byte> bytes) =>
        Inner.WriteAsync(offset, bytes, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        if (_closeOnDispose && _inner is not null)
        {
            await _inner.DisposeAsync();
            _inner = null;
        }
    }

    private bool LosesPower()
    {
        if (!PowerLost && ++OperationCount == PowerLossAtOperation)
        {
            PowerLost = true;
        }

        return PowerLost;
    }

    private async ValueTask TrackedWriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        // Lo que había antes: más allá del final del almacenamiento, ceros.
        var before = new byte[source.Length];
        var existing = (int)Math.Clamp(Inner.Length - offset, 0, source.Length);
        if (existing > 0)
        {
            await Inner.ReadAsync(offset, before.AsMemory(0, existing), cancellationToken);
        }

        await Inner.WriteAsync(offset, source, cancellationToken);
        _unflushed.Add((offset, before, source.ToArray()));
    }

    /// <summary>Cierra de verdad y borra el archivo, si lo hay.</summary>
    public async ValueTask DestroyAsync()
    {
        if (_inner is not null)
        {
            await _inner.DisposeAsync();
            _inner = null;
        }

        if (FilePath is not null)
        {
            System.IO.File.Delete(FilePath);
        }
    }
}
