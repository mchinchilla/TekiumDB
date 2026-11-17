// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;

namespace Tekium.Embedded;

/// <summary>
/// Conditions on when a UUID v7 was created. A UUID v7 starts with the millisecond it was created in, so
/// "everything created between two moments" is a range of the column: of the key itself when the column
/// is the key, with no index on a date.
/// </summary>
/// <remarks>
/// Moments are cut to the millisecond, as the UUID is. The column is assumed to hold UUID v7: any other
/// kind of UUID is compared by the same bytes, which say nothing about when it was created.
/// </remarks>
public static class TkUuidTime
{
    /// <summary>Accepts the rows whose UUID v7 was created between two moments, both included.</summary>
    /// <typeparam name="TRow">The row type.</typeparam>
    /// <param name="field">The column.</param>
    /// <param name="from">The earliest moment accepted.</param>
    /// <param name="to">The latest moment accepted.</param>
    /// <returns>The condition.</returns>
    public static TkFilter<TRow> CreatedBetween<TRow>(this TkField<TRow, Guid> field, DateTimeOffset from, DateTimeOffset to)
        where TRow : class, ITkRow<TRow>
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Span(First(from.ToUnixTimeMilliseconds()), First(to.ToUnixTimeMilliseconds() + 1));
    }

    /// <summary>Accepts the rows whose UUID v7 was created at a moment or after it.</summary>
    /// <typeparam name="TRow">The row type.</typeparam>
    /// <param name="field">The column.</param>
    /// <param name="from">The earliest moment accepted.</param>
    /// <returns>The condition.</returns>
    public static TkFilter<TRow> CreatedSince<TRow>(this TkField<TRow, Guid> field, DateTimeOffset from)
        where TRow : class, ITkRow<TRow>
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Span(First(from.ToUnixTimeMilliseconds()), null);
    }

    /// <summary>Accepts the rows whose UUID v7 was created before the millisecond of a moment.</summary>
    /// <typeparam name="TRow">The row type.</typeparam>
    /// <param name="field">The column.</param>
    /// <param name="to">The moment that is no longer accepted.</param>
    /// <returns>The condition.</returns>
    public static TkFilter<TRow> CreatedBefore<TRow>(this TkField<TRow, Guid> field, DateTimeOffset to)
        where TRow : class, ITkRow<TRow>
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Span(null, First(to.ToUnixTimeMilliseconds()));
    }

    /// <summary>The lowest UUID of a millisecond, as the key encoding writes it.</summary>
    private static byte[] First(long millisecond)
    {
        // The time takes the first six bytes, in network order, and the rest stays zero. No moment a
        // DateTimeOffset holds needs more than those 48 bits; one before 1970 is the start of the range.
        Span<byte> wide = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(wide, Math.Max(millisecond, 0));
        var first = new byte[16];
        wide[2..].CopyTo(first);
        return first;
    }
}
