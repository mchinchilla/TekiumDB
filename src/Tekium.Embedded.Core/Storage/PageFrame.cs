// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

/// <summary>
/// One slot of the page cache. Its pin count is the whole synchronization protocol:
/// zero or more means published and pinned that many times, <see cref="Claimed"/> means one owner
/// is filling or evicting it and nobody else may touch the buffer.
/// </summary>
internal sealed class PageFrame
{
    private const int Claimed = -1;

    private int _pins = Claimed;
    private uint _number;
    private volatile bool _referenced;

    /// <summary>Gets or sets the page bytes. Only the owner of a claimed frame may set it.</summary>
    public byte[]? Buffer { get; set; }

    /// <summary>Gets a value indicating whether the frame exists outside the cache capacity and dies when unpinned.</summary>
    public bool Transient { get; init; }

    public uint Number => Volatile.Read(ref _number);

    /// <summary>Gets or sets a value indicating whether the page was used since the clock hand last passed.</summary>
    public bool Referenced
    {
        get => _referenced;
        set => _referenced = value;
    }

    public bool TryPin(uint number)
    {
        while (true)
        {
            var pins = Volatile.Read(ref _pins);
            if (pins < 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _pins, pins + 1, pins) == pins)
            {
                break;
            }
        }

        // The frame may have been evicted and reused for another page between the lookup and the pin.
        if (Volatile.Read(ref _number) != number)
        {
            Interlocked.Decrement(ref _pins);
            return false;
        }

        // Read before writing: hot pages are pinned from many cores and this line would bounce between them.
        if (!_referenced)
        {
            _referenced = true;
        }

        return true;
    }

    public int Unpin() => Interlocked.Decrement(ref _pins);

    public bool TryClaim() => Interlocked.CompareExchange(ref _pins, Claimed, 0) == 0;

    public void SetNumber(uint number) => Volatile.Write(ref _number, number);

    public void Publish(uint number, int pins)
    {
        Volatile.Write(ref _number, number);
        _referenced = true;
        Volatile.Write(ref _pins, pins);
    }

    /// <summary>Makes a claimed frame claimable again without publishing it.</summary>
    public void Reset()
    {
        Volatile.Write(ref _number, 0);
        _referenced = false;
        Volatile.Write(ref _pins, 0);
    }
}
