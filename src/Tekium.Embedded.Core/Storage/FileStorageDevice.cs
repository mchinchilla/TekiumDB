// Copyright (c) Tekium. Licensed under the MIT License.

using Microsoft.Win32.SafeHandles;

namespace Tekium.Embedded.Storage;

/// <summary>A file accessed with <see cref="RandomAccess"/>: no stream state, no intermediate buffer.</summary>
internal sealed class FileStorageDevice : IStorageDevice
{
    private readonly SafeFileHandle _handle;
    private long _length;

    private FileStorageDevice(SafeFileHandle handle)
    {
        _handle = handle;
        _length = RandomAccess.GetLength(handle);
    }

    public long Length => Volatile.Read(ref _length);

    public static FileStorageDevice Open(string path, FileMode mode) =>
        new(File.OpenHandle(
            path, mode, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous | FileOptions.RandomAccess));

    public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            var read = await RandomAccess.ReadAsync(_handle, destination, offset, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            destination = destination[read..];
            offset += read;
        }
    }

    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        await RandomAccess.WriteAsync(_handle, source, offset, cancellationToken).ConfigureAwait(false);
        var end = offset + source.Length;
        if (end > _length)
        {
            Volatile.Write(ref _length, end);
        }
    }

    public void SetLength(long length)
    {
        RandomAccess.SetLength(_handle, length);
        Volatile.Write(ref _length, length);
    }

    // FlushToDisk only exists in synchronous form and blocks for milliseconds, so it leaves the caller's thread.
    public async ValueTask FlushAsync(CancellationToken cancellationToken) =>
        await Task.Factory.StartNew(
            static handle => RandomAccess.FlushToDisk((SafeFileHandle)handle!),
            _handle,
            cancellationToken,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).ConfigureAwait(false);

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return default;
    }
}
