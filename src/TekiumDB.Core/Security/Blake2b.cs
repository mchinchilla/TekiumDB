// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace TekiumDB.Security;

/// <summary>
/// BLAKE2b (RFC 7693), unkeyed, with an output of 1 to 64 bytes. It exists because Argon2 is built on
/// it and .NET does not ship it. It is not used for anything else.
/// </summary>
internal sealed class Blake2b
{
    public const int MaxOutputSize = 64;

    private const int BlockSize = 128;

    private static readonly ulong[] InitializationVector =
    [
        0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1,
        0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179,
    ];

    private readonly ulong[] _state = new ulong[8];
    private readonly byte[] _buffer = new byte[BlockSize];
    private readonly int _outputSize;
    private int _buffered;
    private ulong _bytesCompressed;

    public Blake2b(int outputSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(outputSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(outputSize, MaxOutputSize);
        _outputSize = outputSize;
        InitializationVector.CopyTo(_state, 0);

        // Parameter block: digest length, no key, fanout 1, depth 1.
        _state[0] ^= 0x01010000UL ^ (uint)outputSize;
    }

    // Message word schedule: row r is the permutation of round r; rounds 10 and 11 reuse rows 0 and 1.
    private static ReadOnlySpan<byte> Sigma =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
        11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
        7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
        9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
        2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
        12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
        13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
        6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
        10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
    ];

    /// <summary>Hashes <paramref name="data"/> into <paramref name="output"/>, whose length is the digest size.</summary>
    public static void Hash(ReadOnlySpan<byte> data, Span<byte> output)
    {
        var hasher = new Blake2b(output.Length);
        hasher.Update(data);
        hasher.Finish(output);
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            // A full buffer is compressed only when more data follows: the last block is special.
            if (_buffered == BlockSize)
            {
                _bytesCompressed += BlockSize;
                Compress(last: false);
                _buffered = 0;
            }

            var take = Math.Min(BlockSize - _buffered, data.Length);
            data[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            data = data[take..];
        }
    }

    /// <summary>Writes the digest and wipes the internal state. The instance cannot be used again.</summary>
    public void Finish(Span<byte> output)
    {
        if (output.Length != _outputSize)
        {
            throw new ArgumentException($"The output must be exactly {_outputSize} bytes.", nameof(output));
        }

        _bytesCompressed += (ulong)_buffered;
        _buffer.AsSpan(_buffered).Clear();
        Compress(last: true);

        Span<byte> digest = stackalloc byte[MaxOutputSize];
        for (var i = 0; i < _state.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(digest[(i * sizeof(ulong))..], _state[i]);
        }

        digest[.._outputSize].CopyTo(output);
        CryptographicOperations.ZeroMemory(digest);
        CryptographicOperations.ZeroMemory(_buffer);
        Array.Clear(_state);
    }

    private static void Mix(ref ulong a, ref ulong b, ref ulong c, ref ulong d, ulong x, ulong y)
    {
        a += b + x;
        d = BitOperations.RotateRight(d ^ a, 32);
        c += d;
        b = BitOperations.RotateRight(b ^ c, 24);
        a += b + y;
        d = BitOperations.RotateRight(d ^ a, 16);
        c += d;
        b = BitOperations.RotateRight(b ^ c, 63);
    }

    private void Compress(bool last)
    {
        Span<ulong> m = stackalloc ulong[16];
        Span<ulong> v = stackalloc ulong[16];
        for (var i = 0; i < 16; i++)
        {
            m[i] = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(i * sizeof(ulong)));
        }

        _state.CopyTo(v);
        InitializationVector.CopyTo(v[8..]);

        // The message is far below 2^64 bytes, so the high half of the 128-bit counter stays zero.
        v[12] ^= _bytesCompressed;
        if (last)
        {
            v[14] = ~v[14];
        }

        for (var round = 0; round < 12; round++)
        {
            var s = Sigma.Slice((round % 10) * 16, 16);
            Mix(ref v[0], ref v[4], ref v[8], ref v[12], m[s[0]], m[s[1]]);
            Mix(ref v[1], ref v[5], ref v[9], ref v[13], m[s[2]], m[s[3]]);
            Mix(ref v[2], ref v[6], ref v[10], ref v[14], m[s[4]], m[s[5]]);
            Mix(ref v[3], ref v[7], ref v[11], ref v[15], m[s[6]], m[s[7]]);
            Mix(ref v[0], ref v[5], ref v[10], ref v[15], m[s[8]], m[s[9]]);
            Mix(ref v[1], ref v[6], ref v[11], ref v[12], m[s[10]], m[s[11]]);
            Mix(ref v[2], ref v[7], ref v[8], ref v[13], m[s[12]], m[s[13]]);
            Mix(ref v[3], ref v[4], ref v[9], ref v[14], m[s[14]], m[s[15]]);
        }

        for (var i = 0; i < 8; i++)
        {
            _state[i] ^= v[i] ^ v[i + 8];
        }

        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(m));
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(v));
    }
}
