// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Tekium.Embedded.Vectors;

/// <summary>The bytes a key of a vector index is stored as. No reflection: the type is tested, never inspected.</summary>
internal static class VectorKey<TKey>
    where TKey : notnull
{
    public static bool IsSupported =>
        typeof(TKey) == typeof(Guid) || typeof(TKey) == typeof(int) || typeof(TKey) == typeof(long) || typeof(TKey) == typeof(string);

    public static byte[] Encode(TKey key)
    {
        if (typeof(TKey) == typeof(Guid))
        {
            return Unsafe.As<TKey, Guid>(ref key).ToByteArray(bigEndian: true);
        }

        if (typeof(TKey) == typeof(int))
        {
            var bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, Unsafe.As<TKey, int>(ref key));
            return bytes;
        }

        if (typeof(TKey) == typeof(long))
        {
            var bytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, Unsafe.As<TKey, long>(ref key));
            return bytes;
        }

        var text = Unsafe.As<TKey, string>(ref key);
        ArgumentException.ThrowIfNullOrEmpty(text, nameof(key));
        return Encoding.UTF8.GetBytes(text);
    }

    public static TKey Decode(ReadOnlySpan<byte> bytes)
    {
        if (typeof(TKey) == typeof(Guid))
        {
            var value = new Guid(bytes, bigEndian: true);
            return Unsafe.As<Guid, TKey>(ref value);
        }

        if (typeof(TKey) == typeof(int))
        {
            var value = BinaryPrimitives.ReadInt32BigEndian(bytes);
            return Unsafe.As<int, TKey>(ref value);
        }

        if (typeof(TKey) == typeof(long))
        {
            var value = BinaryPrimitives.ReadInt64BigEndian(bytes);
            return Unsafe.As<long, TKey>(ref value);
        }

        var text = Encoding.UTF8.GetString(bytes);
        return Unsafe.As<string, TKey>(ref text);
    }
}
