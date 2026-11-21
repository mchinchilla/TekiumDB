// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TekiumDB.Security;

/// <summary>
/// Argon2 version 1.3 (RFC 9106) in managed code, so the package needs no native binary. Validated
/// against the vectors of the RFC and against the reference implementation.
/// </summary>
/// <remarks>
/// Lanes are filled one after another on the calling thread. That does not change the result: within a
/// slice a lane only reads blocks of other lanes that earlier slices finished.
/// </remarks>
internal static class Argon2
{
    private const int Version = 0x13;
    private const int BlockWords = 128;
    private const int BlockBytes = BlockWords * sizeof(ulong);
    private const int SlicesPerPass = 4;
    private const int MinimumSaltSize = 8;
    private const int MinimumOutputSize = 4;
    private const int MaximumParallelism = 0xFFFFFF;

    public static void Derive(
        Argon2Type type,
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> associatedData,
        Argon2Parameters parameters,
        Span<byte> output)
    {
        var lanes = parameters.Parallelism;
        ArgumentOutOfRangeException.ThrowIfLessThan(lanes, 1, nameof(parameters));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lanes, MaximumParallelism, nameof(parameters));
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.Iterations, 1, nameof(parameters));
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.MemoryKiB, 8L * lanes, nameof(parameters));
        if (salt.Length < MinimumSaltSize)
        {
            throw new ArgumentException($"The salt must be at least {MinimumSaltSize} bytes.", nameof(salt));
        }

        if (output.Length < MinimumOutputSize)
        {
            throw new ArgumentException($"The output must be at least {MinimumOutputSize} bytes.", nameof(output));
        }

        // Memory is rounded down to a multiple of four slices per lane.
        var segmentLength = parameters.MemoryKiB / (lanes * SlicesPerPass);
        var laneLength = segmentLength * SlicesPerPass;
        var blocks = laneLength * lanes;
        var context = new Context(type, lanes, laneLength, segmentLength, blocks, parameters.Iterations);

        Span<byte> seed = stackalloc byte[Blake2b.MaxOutputSize + (2 * sizeof(uint))];
        Span<byte> blockBytes = stackalloc byte[BlockBytes];
        var memory = GC.AllocateUninitializedArray<ulong>(checked(blocks * BlockWords));
        try
        {
            InitialHash(type, password, salt, secret, associatedData, parameters, output.Length, seed);

            // The first two blocks of every lane come straight from the seed.
            for (var lane = 0; lane < lanes; lane++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(seed[(Blake2b.MaxOutputSize + sizeof(uint))..], (uint)lane);
                for (var column = 0; column < 2; column++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(seed[Blake2b.MaxOutputSize..], (uint)column);
                    LongHash(seed, blockBytes);
                    var block = memory.AsSpan(((lane * laneLength) + column) * BlockWords, BlockWords);
                    for (var word = 0; word < BlockWords; word++)
                    {
                        block[word] = BinaryPrimitives.ReadUInt64LittleEndian(blockBytes[(word * sizeof(ulong))..]);
                    }
                }
            }

            for (var pass = 0; pass < parameters.Iterations; pass++)
            {
                for (var slice = 0; slice < SlicesPerPass; slice++)
                {
                    for (var lane = 0; lane < lanes; lane++)
                    {
                        FillSegment(memory, context, pass, slice, lane);
                    }
                }
            }

            // The tag is the long hash of the XOR of the last block of every lane.
            Span<ulong> final = stackalloc ulong[BlockWords];
            memory.AsSpan((laneLength - 1) * BlockWords, BlockWords).CopyTo(final);
            for (var lane = 1; lane < lanes; lane++)
            {
                var last = memory.AsSpan((((lane + 1) * laneLength) - 1) * BlockWords, BlockWords);
                for (var word = 0; word < BlockWords; word++)
                {
                    final[word] ^= last[word];
                }
            }

            for (var word = 0; word < BlockWords; word++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(blockBytes[(word * sizeof(ulong))..], final[word]);
            }

            LongHash(blockBytes, output);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(final));
        }
        finally
        {
            // Everything here is derived from the password.
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(memory.AsSpan()));
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(blockBytes);
        }
    }

    private static void InitialHash(
        Argon2Type type,
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> associatedData,
        Argon2Parameters parameters,
        int outputLength,
        Span<byte> seed)
    {
        var hasher = new Blake2b(Blake2b.MaxOutputSize);
        Append(hasher, parameters.Parallelism);
        Append(hasher, outputLength);
        Append(hasher, parameters.MemoryKiB);
        Append(hasher, parameters.Iterations);
        Append(hasher, Version);
        Append(hasher, (int)type);
        Append(hasher, password.Length);
        hasher.Update(password);
        Append(hasher, salt.Length);
        hasher.Update(salt);
        Append(hasher, secret.Length);
        hasher.Update(secret);
        Append(hasher, associatedData.Length);
        hasher.Update(associatedData);
        hasher.Finish(seed[..Blake2b.MaxOutputSize]);

        static void Append(Blake2b hasher, int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
            hasher.Update(bytes);
        }
    }

    /// <summary>The variable-length hash H' of RFC 9106: BLAKE2b chained to produce any output length.</summary>
    private static void LongHash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)output.Length);
        if (output.Length <= Blake2b.MaxOutputSize)
        {
            var single = new Blake2b(output.Length);
            single.Update(length);
            single.Update(input);
            single.Finish(output);
            return;
        }

        // Each link contributes its first 32 bytes; the last one contributes all of its bytes.
        const int Half = Blake2b.MaxOutputSize / 2;
        Span<byte> link = stackalloc byte[Blake2b.MaxOutputSize];
        var first = new Blake2b(Blake2b.MaxOutputSize);
        first.Update(length);
        first.Update(input);
        first.Finish(link);
        link[..Half].CopyTo(output);
        var written = Half;
        while (output.Length - written > Blake2b.MaxOutputSize)
        {
            Blake2b.Hash(link, link);
            link[..Half].CopyTo(output[written..]);
            written += Half;
        }

        var remaining = output.Length - written;
        Blake2b.Hash(link, link[..remaining]);
        link[..remaining].CopyTo(output[written..]);
        CryptographicOperations.ZeroMemory(link);
    }

    private static void FillSegment(ulong[] memory, in Context context, int pass, int slice, int lane)
    {
        var independent = context.Type == Argon2Type.Argon2i
            || (context.Type == Argon2Type.Argon2id && pass == 0 && slice < SlicesPerPass / 2);

        Span<ulong> zero = stackalloc ulong[BlockWords];
        Span<ulong> input = stackalloc ulong[BlockWords];
        Span<ulong> addresses = stackalloc ulong[BlockWords];
        zero.Clear();
        input.Clear();
        if (independent)
        {
            input[0] = (ulong)pass;
            input[1] = (ulong)lane;
            input[2] = (ulong)slice;
            input[3] = (ulong)context.Blocks;
            input[4] = (ulong)context.Iterations;
            input[5] = (ulong)context.Type;
        }

        var start = 0;
        if (pass == 0 && slice == 0)
        {
            // The first two blocks of the lane are already filled.
            start = 2;
            if (independent)
            {
                NextAddresses(zero, input, addresses);
            }
        }

        var laneLength = context.LaneLength;
        var current = (lane * laneLength) + (slice * context.SegmentLength) + start;
        var previous = current % laneLength == 0 ? current + laneLength - 1 : current - 1;
        for (var index = start; index < context.SegmentLength; index++, current++, previous++)
        {
            if (current % laneLength == 1)
            {
                previous = current - 1;
            }

            ulong random;
            if (independent)
            {
                if (index % BlockWords == 0)
                {
                    NextAddresses(zero, input, addresses);
                }

                random = addresses[index % BlockWords];
            }
            else
            {
                random = memory[previous * BlockWords];
            }

            var referenceLane = pass == 0 && slice == 0 ? lane : (int)((random >> 32) % (ulong)context.Lanes);
            var referenceIndex = ReferenceIndex(context, pass, slice, index, (uint)random, referenceLane == lane);
            FillBlock(
                memory.AsSpan(previous * BlockWords, BlockWords),
                memory.AsSpan(((referenceLane * laneLength) + referenceIndex) * BlockWords, BlockWords),
                memory.AsSpan(current * BlockWords, BlockWords),
                xorWithExisting: pass > 0);
        }

        static void NextAddresses(ReadOnlySpan<ulong> zero, Span<ulong> input, Span<ulong> addresses)
        {
            input[6]++;
            FillBlock(zero, input, addresses, xorWithExisting: false);
            FillBlock(zero, addresses, addresses, xorWithExisting: false);
        }
    }

    /// <summary>Maps a pseudo-random value to a block the current one may reference, favouring recent blocks.</summary>
    private static int ReferenceIndex(in Context context, int pass, int slice, int index, uint random, bool sameLane)
    {
        long area;
        if (pass == 0)
        {
            if (slice == 0)
            {
                area = index - 1;
            }
            else if (sameLane)
            {
                area = (slice * context.SegmentLength) + index - 1;
            }
            else
            {
                area = (slice * context.SegmentLength) + (index == 0 ? -1 : 0);
            }
        }
        else if (sameLane)
        {
            area = context.LaneLength - context.SegmentLength + index - 1;
        }
        else
        {
            area = context.LaneLength - context.SegmentLength + (index == 0 ? -1 : 0);
        }

        var relative = ((ulong)random * random) >> 32;
        relative = (ulong)area - 1 - (((ulong)area * relative) >> 32);
        var start = pass != 0 && slice != SlicesPerPass - 1 ? (slice + 1) * context.SegmentLength : 0;
        return (int)(((ulong)start + relative) % (ulong)context.LaneLength);
    }

    /// <summary>The compression function G: <paramref name="next"/> = P(previous ^ reference) ^ previous ^ reference.</summary>
    private static void FillBlock(
        ReadOnlySpan<ulong> previous, ReadOnlySpan<ulong> reference, Span<ulong> next, bool xorWithExisting)
    {
        Span<ulong> mixed = stackalloc ulong[BlockWords];
        Span<ulong> carry = stackalloc ulong[BlockWords];
        for (var i = 0; i < BlockWords; i++)
        {
            mixed[i] = previous[i] ^ reference[i];
            carry[i] = xorWithExisting ? mixed[i] ^ next[i] : mixed[i];
        }

        // The block is an 8×8 matrix of 16-byte cells: permute every row, then every column.
        for (var row = 0; row < BlockWords; row += 16)
        {
            Permute(
                mixed, row, row + 1, row + 2, row + 3, row + 4, row + 5, row + 6, row + 7,
                row + 8, row + 9, row + 10, row + 11, row + 12, row + 13, row + 14, row + 15);
        }

        for (var column = 0; column < 16; column += 2)
        {
            Permute(
                mixed, column, column + 1, column + 16, column + 17, column + 32, column + 33, column + 48, column + 49,
                column + 64, column + 65, column + 80, column + 81, column + 96, column + 97, column + 112, column + 113);
        }

        for (var i = 0; i < BlockWords; i++)
        {
            next[i] = carry[i] ^ mixed[i];
        }
    }

    /// <summary>One BLAKE2b round without message words, using the multiplicative mix of Argon2.</summary>
    private static void Permute(
        Span<ulong> v, int i0, int i1, int i2, int i3, int i4, int i5, int i6, int i7,
        int i8, int i9, int i10, int i11, int i12, int i13, int i14, int i15)
    {
        Mix(ref v[i0], ref v[i4], ref v[i8], ref v[i12]);
        Mix(ref v[i1], ref v[i5], ref v[i9], ref v[i13]);
        Mix(ref v[i2], ref v[i6], ref v[i10], ref v[i14]);
        Mix(ref v[i3], ref v[i7], ref v[i11], ref v[i15]);
        Mix(ref v[i0], ref v[i5], ref v[i10], ref v[i15]);
        Mix(ref v[i1], ref v[i6], ref v[i11], ref v[i12]);
        Mix(ref v[i2], ref v[i7], ref v[i8], ref v[i13]);
        Mix(ref v[i3], ref v[i4], ref v[i9], ref v[i14]);
    }

    private static void Mix(ref ulong a, ref ulong b, ref ulong c, ref ulong d)
    {
        a += b + (2 * (ulong)(uint)a * (uint)b);
        d = BitOperations.RotateRight(d ^ a, 32);
        c += d + (2 * (ulong)(uint)c * (uint)d);
        b = BitOperations.RotateRight(b ^ c, 24);
        a += b + (2 * (ulong)(uint)a * (uint)b);
        d = BitOperations.RotateRight(d ^ a, 16);
        c += d + (2 * (ulong)(uint)c * (uint)d);
        b = BitOperations.RotateRight(b ^ c, 63);
    }

    private readonly record struct Context(
        Argon2Type Type, int Lanes, int LaneLength, int SegmentLength, int Blocks, int Iterations);
}
