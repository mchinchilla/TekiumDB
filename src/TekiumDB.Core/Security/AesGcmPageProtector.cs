// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using TekiumDB.Storage;

namespace TekiumDB.Security;

/// <summary>
/// AES-256-GCM per page. The 16-byte page header stays in the clear but is authenticated, the payload is
/// encrypted, and the tag fills the trailer: reading a page verifies it in the same operation that decrypts it.
/// </summary>
/// <remarks>
/// The nonce is the page number followed by the id of the transaction that wrote the page: the twelve
/// bytes at offset 4 of the header. GCM is broken by a single nonce reuse under one key, so the pager
/// guarantees that pair is never written twice; see <c>Pager.BeginWriteAsync</c>. Do not change either
/// side without reviewing the other.
/// What it does not protect against: replacing a page with an older version of the same page.
/// </remarks>
internal sealed class AesGcmPageProtector : IPageProtector
{
    public const int KeySize = 32;

    private const int TagSize = PageLayout.TrailerSize;
    private const int NonceOffset = 4;
    private const int NonceSize = 12;
    private const int MaxPooledCiphers = 64;

    // Pinned so the garbage collector never leaves copies of key material behind when it compacts.
    private readonly byte[] _fileKey = GC.AllocateArray<byte>(KeySize, pinned: true);
    private readonly byte[] _pageKey = GC.AllocateArray<byte>(KeySize, pinned: true);
    private readonly byte[] _macKey = GC.AllocateArray<byte>(KeySize, pinned: true);

    // AesGcm instances are not documented as thread-safe, and pages are decrypted from many threads.
    private readonly ConcurrentBag<AesGcm> _ciphers = [];
    private bool _disposed;

    public AesGcmPageProtector(ReadOnlySpan<byte> fileKey)
    {
        if (fileKey.Length != KeySize)
        {
            throw new ArgumentException($"The file key must be {KeySize} bytes.", nameof(fileKey));
        }

        if (!AesGcm.IsSupported)
        {
            throw new PlatformNotSupportedException("AES-GCM is not available on this platform.");
        }

        // The file key never touches data: one derived key encrypts pages, another authenticates header records.
        fileKey.CopyTo(_fileKey);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, _pageKey, salt: default, "TekiumDB page encryption v1"u8);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, _macKey, salt: default, "TekiumDB header authentication v1"u8);
    }

    public bool Encrypts => true;

    public int Protect(ReadOnlySpan<byte> page, Span<byte> stored)
    {
        var header = stored[..PageLayout.HeaderSize];
        page[..PageLayout.HeaderSize].CopyTo(header);
        var cipher = Rent();
        try
        {
            cipher.Encrypt(
                header.Slice(NonceOffset, NonceSize),
                page[PageLayout.HeaderSize..^TagSize],
                stored[PageLayout.HeaderSize..(page.Length - TagSize)],
                stored.Slice(page.Length - TagSize, TagSize),
                header);
        }
        finally
        {
            Return(cipher);
        }

        return page.Length;
    }

    public void Unprotect(Span<byte> page, PageNumber expected)
    {
        var header = page[..PageLayout.HeaderSize];
        var body = page[PageLayout.HeaderSize..^TagSize];
        var cipher = Rent();
        try
        {
            cipher.Decrypt(header.Slice(NonceOffset, NonceSize), body, page[^TagSize..], body, header);
        }
        catch (CryptographicException)
        {
            // The body is unspecified after a failed decryption: never let it be read.
            body.Clear();
            throw new InvalidDataException(
                $"Page {expected.Value} failed authentication: the file is corrupt or was altered.");
        }
        finally
        {
            Return(cipher);
        }

        if (PageLayout.Number(page) != expected)
        {
            throw new InvalidDataException(
                $"Page {expected.Value} holds the contents of page {PageLayout.Number(page).Value}: the file is corrupt or was altered.");
        }
    }

    public void Authenticate(ReadOnlySpan<byte> data, Span<byte> check)
    {
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_macKey, data, mac);
        mac[..check.Length].CopyTo(check);
        CryptographicOperations.ZeroMemory(mac);
    }

    /// <summary>Wraps the file key under a new password into a header copy. Used to change the password.</summary>
    public void SealEnvelope(Span<byte> header, ReadOnlySpan<byte> password, Argon2Parameters parameters) =>
        KeyEnvelope.Seal(header, password, parameters, _fileKey);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_fileKey);
        CryptographicOperations.ZeroMemory(_pageKey);
        CryptographicOperations.ZeroMemory(_macKey);
        while (_ciphers.TryTake(out var cipher))
        {
            cipher.Dispose();
        }
    }

    private AesGcm Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _ciphers.TryTake(out var cipher) ? cipher : new AesGcm(_pageKey, TagSize);
    }

    private void Return(AesGcm cipher)
    {
        if (_disposed || _ciphers.Count >= MaxPooledCiphers)
        {
            cipher.Dispose();
        }
        else
        {
            _ciphers.Add(cipher);
        }
    }
}
