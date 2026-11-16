// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

/// <summary>Random-access byte storage under the pager. The pager does not know whether it is a file.</summary>
internal interface IStorageDevice : IAsyncDisposable
{
    /// <summary>Gets the current length in bytes.</summary>
    long Length { get; }

    /// <summary>Fills <paramref name="destination"/> completely or throws <see cref="EndOfStreamException"/>.</summary>
    ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);

    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken);

    /// <summary>Sets the length. New space reads as zeros. The operating system offers no asynchronous form.</summary>
    void SetLength(long length);

    /// <summary>Returns once everything written so far is on stable storage.</summary>
    ValueTask FlushAsync(CancellationToken cancellationToken);
}
