// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Security.Cryptography;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Security;

/// <summary>
/// The password never encrypts data. It derives a master key that wraps a random file key stored in the
/// header, so changing the password rewrites the header and nothing else.
/// </summary>
/// <remarks>
/// The wrap is AES-256-GCM over the file key, with the leading fields of the header (format, page size,
/// flags, salt, derivation parameters) as associated data: altering any of them makes the unwrap fail.
/// A wrong password and a damaged envelope are indistinguishable, by design.
/// </remarks>
internal static class KeyEnvelope
{
    /// <summary>Upper bounds on what a header may ask for: the parameters are read before anything is authenticated.</summary>
    public const int MaxMemoryKiB = 4 * 1024 * 1024;

    public const int MaxIterations = 1024;
    public const int MaxParallelism = 256;

    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static void Validate(Argon2Parameters parameters)
    {
        if (parameters.Parallelism is < 1 or > MaxParallelism
            || parameters.Iterations is < 1 or > MaxIterations
            || parameters.MemoryKiB > MaxMemoryKiB
            || parameters.MemoryKiB < 8 * parameters.Parallelism)
        {
            throw new InvalidDataException(
                "The key derivation parameters are outside the accepted range: "
                + $"memory up to {MaxMemoryKiB} KiB, up to {MaxIterations} iterations, up to {MaxParallelism} lanes.");
        }
    }

    /// <summary>Writes salt, parameters and the wrapped file key into one copy of the header.</summary>
    public static void Seal(
        Span<byte> header, ReadOnlySpan<byte> password, Argon2Parameters parameters, ReadOnlySpan<byte> fileKey)
    {
        Validate(parameters);
        var salt = header.Slice(FileHeader.SaltOffset, FileHeader.SaltSize);
        RandomNumberGenerator.Fill(salt);
        var stored = header[FileHeader.KeyDerivationOffset..];
        BinaryPrimitives.WriteUInt32LittleEndian(stored, (uint)parameters.MemoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(stored[4..], (uint)parameters.Iterations);
        BinaryPrimitives.WriteUInt32LittleEndian(stored[8..], (uint)parameters.Parallelism);

        var wrap = header.Slice(FileHeader.KeyWrapOffset, FileHeader.KeyWrapSize);
        var nonce = wrap[..NonceSize];
        RandomNumberGenerator.Fill(nonce);
        Span<byte> master = stackalloc byte[AesGcmPageProtector.KeySize];
        try
        {
            Argon2.Derive(Argon2Type.Argon2id, password, salt, default, default, parameters, master);
            using var cipher = new AesGcm(master, TagSize);
            cipher.Encrypt(
                nonce,
                fileKey,
                wrap.Slice(NonceSize, fileKey.Length),
                wrap[^TagSize..],
                header[..FileHeader.AuthenticatedPrefixSize]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    /// <summary>Recovers the file key from one copy of the header. False means wrong password or damaged envelope.</summary>
    public static bool TryOpen(ReadOnlySpan<byte> header, ReadOnlySpan<byte> password, Span<byte> fileKey)
    {
        var stored = header[FileHeader.KeyDerivationOffset..];
        var parameters = new Argon2Parameters(
            ClampToInt(BinaryPrimitives.ReadUInt32LittleEndian(stored)),
            ClampToInt(BinaryPrimitives.ReadUInt32LittleEndian(stored[4..])),
            ClampToInt(BinaryPrimitives.ReadUInt32LittleEndian(stored[8..])));
        Validate(parameters);

        var wrap = header.Slice(FileHeader.KeyWrapOffset, FileHeader.KeyWrapSize);
        Span<byte> master = stackalloc byte[AesGcmPageProtector.KeySize];
        try
        {
            Argon2.Derive(
                Argon2Type.Argon2id,
                password,
                header.Slice(FileHeader.SaltOffset, FileHeader.SaltSize),
                default,
                default,
                parameters,
                master);
            using var cipher = new AesGcm(master, TagSize);
            cipher.Decrypt(
                wrap[..NonceSize],
                wrap.Slice(NonceSize, fileKey.Length),
                wrap[^TagSize..],
                fileKey,
                header[..FileHeader.AuthenticatedPrefixSize]);
            return true;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(fileKey);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }

        static int ClampToInt(uint value) => (int)Math.Min(value, int.MaxValue);
    }
}
