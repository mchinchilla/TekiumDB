// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.CrashChild;

/// <summary>
/// Un dispositivo que mata el proceso en la operación elegida: antes de hacerla, o a medio hacerla si es
/// una escritura. Matar es matar: sin finalizadores, sin cerrar el archivo, sin vaciar nada.
/// </summary>
internal sealed class CrashingDevice(IStorageDevice inner, int crashAt, bool torn) : IStorageDevice
{
    private readonly StringBuilder _operations = new();

    public long Length => inner.Length;

    /// <summary>Gets one letter per operation so far: W for a write, F for a flush, L for a length change.</summary>
    public string Operations => _operations.ToString();

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) =>
        inner.ReadAsync(offset, destination, cancellationToken);

    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        if (IsTheEnd('W'))
        {
            if (torn)
            {
                await inner.WriteAsync(offset, source[..(source.Length / 2)], cancellationToken);
            }

            Die();
        }

        await inner.WriteAsync(offset, source, cancellationToken);
    }

    public void SetLength(long length)
    {
        if (IsTheEnd('L'))
        {
            Die();
        }

        inner.SetLength(length);
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (IsTheEnd('F'))
        {
            Die();
        }

        return inner.FlushAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private static void Die()
    {
        using var self = Process.GetCurrentProcess();
        self.Kill();
        Thread.Sleep(Timeout.Infinite);
    }

    private bool IsTheEnd(char operation)
    {
        _operations.Append(operation);
        return _operations.Length == crashAt;
    }
}
