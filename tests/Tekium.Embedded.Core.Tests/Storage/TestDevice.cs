// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Storage;

/// <summary>
/// Envuelve un dispositivo real del motor (archivo o memoria) para las pruebas: cuenta escrituras y
/// flushes, inyecta fallos de escritura, permite dañar bytes, y se puede volver a usar después de
/// <see cref="DisposeAsync"/> para "reabrir" la misma base.
/// </summary>
internal sealed class TestDevice : IStorageDevice
{
    private readonly Func<IStorageDevice> _open;
    private readonly bool _closeOnDispose;
    private readonly bool _flushToDisk;
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

    /// <summary>Gets or sets the ordinal of the write (1-based, counted from creation) that throws.</summary>
    public int? FailOnWriteNumber { get; set; }

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

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) =>
        Inner.ReadAsync(offset, destination, cancellationToken);

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        WriteCount++;
        if (WriteCount == FailOnWriteNumber)
        {
            return ValueTask.FromException(new IOException("Injected write failure."));
        }

        OnWrite?.Invoke(offset, source);
        return Inner.WriteAsync(offset, source, cancellationToken);
    }

    public void SetLength(long length) => Inner.SetLength(length);

    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        FlushCount++;
        return _flushToDisk ? Inner.FlushAsync(cancellationToken) : default;
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
