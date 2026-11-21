// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace TekiumDB;

/// <summary>
/// Encodes key values so that comparing the bytes compares the values: the B+Tree only compares bytes
/// and knows nothing about types. Generated code calls it; so does a lookup by key.
/// </summary>
public static class TkKey
{
    /// <summary>Writes a <see cref="Guid"/> key: its 16 bytes in network order, the order UUID v7 sorts by time in.</summary>
    /// <param name="value">The key.</param>
    /// <param name="destination">Where it goes.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Write(Guid value, Span<byte> destination) =>
        value.TryWriteBytes(destination, bigEndian: true, out var written) ? written : ThrowTooLong();

    /// <summary>Writes an <see cref="int"/> key: big-endian with the sign bit flipped, so negatives sort first.</summary>
    /// <param name="value">The key.</param>
    /// <param name="destination">Where it goes.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Write(int value, Span<byte> destination) =>
        BinaryPrimitives.TryWriteUInt32BigEndian(destination, (uint)value ^ 0x8000_0000) ? sizeof(int) : ThrowTooLong();

    /// <summary>Writes a <see cref="long"/> key: big-endian with the sign bit flipped, so negatives sort first.</summary>
    /// <param name="value">The key.</param>
    /// <param name="destination">Where it goes.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Write(long value, Span<byte> destination) =>
        BinaryPrimitives.TryWriteUInt64BigEndian(destination, (ulong)value ^ 0x8000_0000_0000_0000)
            ? sizeof(long)
            : ThrowTooLong();

    /// <summary>Writes a <see cref="uint"/> key, big-endian.</summary>
    /// <param name="value">The key.</param>
    /// <param name="destination">Where it goes.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Write(uint value, Span<byte> destination) =>
        BinaryPrimitives.TryWriteUInt32BigEndian(destination, value) ? sizeof(uint) : ThrowTooLong();

    /// <summary>Writes a <see cref="ulong"/> key, big-endian.</summary>
    /// <param name="value">The key.</param>
    /// <param name="destination">Where it goes.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Write(ulong value, Span<byte> destination) =>
        BinaryPrimitives.TryWriteUInt64BigEndian(destination, value) ? sizeof(ulong) : ThrowTooLong();

    /// <summary>
    /// Writes a <see cref="string"/> key as UTF-8, which sorts by code point. The empty string cannot be a key.
    /// </summary>
    /// <param name="value">The key.</param>
    /// <param name="destination">Where it goes.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Write(string value, Span<byte> destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return TkText.ByteCount(value) <= destination.Length && TkText.Utf8.TryGetBytes(value, destination, out var written)
            ? written
            : ThrowTooLong();
    }

    /// <summary>Writes a key given as a type parameter. The branches not taken disappear when the method is compiled.</summary>
    internal static int Write<TKey>(TKey key, TkColumnType expected, Span<byte> destination)
    {
        // Unsafe.As only reinterprets a reference whose exact type the line above has just established.
        if (typeof(TKey) == typeof(Guid) && expected == TkColumnType.Uuid)
        {
            return Write(Unsafe.As<TKey, Guid>(ref key), destination);
        }

        if (typeof(TKey) == typeof(int) && expected == TkColumnType.I32)
        {
            return Write(Unsafe.As<TKey, int>(ref key), destination);
        }

        if (typeof(TKey) == typeof(long) && expected == TkColumnType.I64)
        {
            return Write(Unsafe.As<TKey, long>(ref key), destination);
        }

        if (typeof(TKey) == typeof(uint) && expected == TkColumnType.U32)
        {
            return Write(Unsafe.As<TKey, uint>(ref key), destination);
        }

        if (typeof(TKey) == typeof(ulong) && expected == TkColumnType.U64)
        {
            return Write(Unsafe.As<TKey, ulong>(ref key), destination);
        }

        if (typeof(TKey) == typeof(string) && expected == TkColumnType.Text)
        {
            return Write(Unsafe.As<TKey, string>(ref key), destination);
        }

        throw new ArgumentException($"The key of the table is a {expected}, not a {typeof(TKey).Name}.", nameof(key));
    }

    /// <summary>A stored key as text, to name a row in an error.</summary>
    internal static string Format(ReadOnlySpan<byte> stored, TkColumnType type)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return type switch
        {
            TkColumnType.Uuid when stored.Length == 16 => new Guid(stored, bigEndian: true).ToString(),
            TkColumnType.I32 when stored.Length == sizeof(int) =>
                ((int)(BinaryPrimitives.ReadUInt32BigEndian(stored) ^ 0x8000_0000)).ToString(culture),
            TkColumnType.I64 when stored.Length == sizeof(long) =>
                ((long)(BinaryPrimitives.ReadUInt64BigEndian(stored) ^ 0x8000_0000_0000_0000)).ToString(culture),
            TkColumnType.U32 when stored.Length == sizeof(uint) => BinaryPrimitives.ReadUInt32BigEndian(stored).ToString(culture),
            TkColumnType.U64 when stored.Length == sizeof(ulong) => BinaryPrimitives.ReadUInt64BigEndian(stored).ToString(culture),
            TkColumnType.Text => System.Text.Encoding.UTF8.GetString(stored),
            _ => Convert.ToHexString(stored),
        };
    }

    /// <summary>Whether a column of this type can be the key of a table.</summary>
    internal static bool IsSupported(TkColumnType type) =>
        type is TkColumnType.Uuid or TkColumnType.I32 or TkColumnType.I64 or TkColumnType.U32
            or TkColumnType.U64 or TkColumnType.Text;

    private static int ThrowTooLong() =>
        throw new ArgumentException("The key is longer than a key may be.");
}
