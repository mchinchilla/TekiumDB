// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

/// <summary>
/// The storage device of an in-memory database: same contract as a file, nothing survives disposal.
/// Every operation completes synchronously and none allocates, except to back bytes written for the first time.
/// </summary>
/// <remarks>
/// Memory is held in fixed-size blocks allocated when first written, so reserving length costs nothing
/// and growing never copies data. A block that was never written reads as zeros.
/// Like a file, it does not arbitrate between a read and a write of the same bytes at the same time;
/// the pager never does that.
/// </remarks>
internal sealed class MemoryStorageDevice : IStorageDevice
{
    // 64 KiB: below the large-object threshold, and a multiple of every page size.
    private const int BlockBits = 16;
    private const int BlockSize = 1 << BlockBits;
    private const int BlockMask = BlockSize - 1;

    private byte[]?[] _blocks = [];
    private long _length;

    public long Length => Volatile.Read(ref _length);

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        if (offset < 0 || offset + destination.Length > Length)
        {
            return ValueTask.FromException(new EndOfStreamException());
        }

        var blocks = Volatile.Read(ref _blocks);
        var target = destination.Span;
        while (!target.IsEmpty)
        {
            var index = (int)(offset >> BlockBits);
            var within = (int)(offset & BlockMask);
            var count = Math.Min(target.Length, BlockSize - within);
            var block = index < blocks.Length ? Volatile.Read(ref blocks[index]) : null;
            if (block is null)
            {
                target[..count].Clear();
            }
            else
            {
                block.AsSpan(within, count).CopyTo(target);
            }

            target = target[count..];
            offset += count;
        }

        return default;
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var end = offset + source.Length;
        var blocks = EnsureTable(end);
        var remaining = source.Span;
        while (!remaining.IsEmpty)
        {
            var index = (int)(offset >> BlockBits);
            var within = (int)(offset & BlockMask);
            var count = Math.Min(remaining.Length, BlockSize - within);
            var block = blocks[index];
            if (block is null)
            {
                block = new byte[BlockSize];
                Volatile.Write(ref blocks[index], block);
            }

            remaining[..count].CopyTo(block.AsSpan(within));
            remaining = remaining[count..];
            offset += count;
        }

        if (end > _length)
        {
            Volatile.Write(ref _length, end);
        }

        return default;
    }

    public void SetLength(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var current = _length;
        if (length < current)
        {
            // What is cut off must read as zeros if the device grows again.
            var blocks = _blocks;
            var firstWhole = (int)((length + BlockMask) >> BlockBits);
            for (var i = firstWhole; i < blocks.Length; i++)
            {
                Volatile.Write(ref blocks[i], null);
            }

            var partial = (int)(length >> BlockBits);
            if ((length & BlockMask) != 0 && partial < blocks.Length)
            {
                blocks[partial]?.AsSpan((int)(length & BlockMask)).Clear();
            }
        }

        Volatile.Write(ref _length, length);
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? ValueTask.FromCanceled(cancellationToken) : default;

    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _length, 0);
        Volatile.Write(ref _blocks, []);
        return default;
    }

    /// <summary>Makes the block table cover <paramref name="bytes"/>. Blocks themselves are created on first write.</summary>
    private byte[]?[] EnsureTable(long bytes)
    {
        var blocks = _blocks;
        var needed = checked((int)((bytes + BlockMask) >> BlockBits));
        if (needed > blocks.Length)
        {
            var grown = new byte[]?[Math.Max(needed, blocks.Length * 2)];
            blocks.CopyTo(grown, 0);
            Volatile.Write(ref _blocks, grown);
            blocks = grown;
        }

        return blocks;
    }
}
