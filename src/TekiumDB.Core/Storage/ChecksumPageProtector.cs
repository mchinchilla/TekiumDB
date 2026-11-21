// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.IO.Hashing;

namespace TekiumDB.Storage;

/// <summary>Unencrypted mode: pages are stored as they are, with an XxHash3 that detects corruption.</summary>
internal sealed class ChecksumPageProtector : IPageProtector
{
    private ChecksumPageProtector()
    {
    }

    public static ChecksumPageProtector Instance { get; } = new();

    public bool Encrypts => false;

    public int Protect(ReadOnlySpan<byte> page, Span<byte> stored)
    {
        page.CopyTo(stored);
        PageLayout.Seal(stored[..page.Length]);
        return page.Length;
    }

    public void Unprotect(Span<byte> page, PageNumber expected) => PageLayout.Verify(page, expected);

    public void Authenticate(ReadOnlySpan<byte> data, Span<byte> check)
    {
        check.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(check, XxHash3.HashToUInt64(data));
    }

    public void Dispose()
    {
    }
}
