// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;

namespace Tekium.Embedded.Storage;

/// <summary>
/// Zstandard per page, in front of whatever protects the page: compress first, then checksum or encrypt,
/// never the other way round, because ciphertext does not compress.
/// </summary>
/// <remarks>
/// A compressed page is stored as its header, the compressed payload and the check, back to back: shorter
/// than a page. The header says how long the compressed payload is, in the clear, which is how the reader
/// knows where the check is; a wrong length fails that check. A page that does not get smaller, and the
/// pages of the free-list, are stored as they are, with a length of zero.
/// The page still owns its whole slot in the file. What compression saves is bytes written and bytes
/// encrypted, not file size.
/// </remarks>
internal sealed class CompressedPageProtector : IPageProtector
{
    /// <summary>Where the header keeps the length of the compressed payload. Zero means not compressed.</summary>
    private const int LengthOffset = 2;

    private readonly int _quality;
    private readonly int _windowLog2;

    public CompressedPageProtector(IPageProtector inner, int quality, PageSize pageSize)
    {
        Inner = inner;
        _quality = Math.Clamp(quality, ZstandardCompressionOptions.MinQuality, ZstandardCompressionOptions.MaxQuality);

        // A window as large as the page: measured, a smaller one compresses a third worse and takes
        // nearly twice as long to decompress; a larger one changes nothing.
        _windowLog2 = Math.Clamp(
            BitOperations.Log2((uint)pageSize.Bytes),
            ZstandardCompressionOptions.MinWindowLog2,
            ZstandardCompressionOptions.MaxWindowLog2);
    }

    public IPageProtector Inner { get; }

    public bool Encrypts => Inner.Encrypts;

    public int Protect(ReadOnlySpan<byte> page, Span<byte> stored)
    {
        var type = PageLayout.Type(page);
        if (type is PageType.FreeListTrunk or PageType.FreeListDirectory)
        {
            return Inner.Protect(page, stored);
        }

        var payload = page[PageLayout.HeaderSize..^PageLayout.TrailerSize];
        var scratch = ArrayPool<byte>.Shared.Rent(page.Length);
        try
        {
            // Worth it only if it saves something: the destination is one byte short of the payload.
            var body = scratch.AsSpan(PageLayout.HeaderSize, payload.Length - 1);
            if (!ZstandardEncoder.TryCompress(payload, body, out var length, _quality, _windowLog2) || length == 0)
            {
                return Inner.Protect(page, stored);
            }

            var shortened = scratch.AsSpan(0, PageLayout.HeaderSize + length + PageLayout.TrailerSize);
            page[..PageLayout.HeaderSize].CopyTo(shortened);
            BinaryPrimitives.WriteUInt16LittleEndian(shortened[LengthOffset..], (ushort)length);
            shortened[^PageLayout.TrailerSize..].Clear();
            return Inner.Protect(shortened, stored);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public void Unprotect(Span<byte> page, PageNumber expected)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(page[LengthOffset..]);
        if (length == 0)
        {
            Inner.Unprotect(page, expected);
            return;
        }

        var payload = page[PageLayout.HeaderSize..^PageLayout.TrailerSize];
        if (length >= payload.Length)
        {
            throw Damaged(expected);
        }

        // The check covers the header, so a length that was tampered with does not get past this.
        Inner.Unprotect(page[..(PageLayout.HeaderSize + length + PageLayout.TrailerSize)], expected);

        var scratch = ArrayPool<byte>.Shared.Rent(payload.Length);
        try
        {
            if (!ZstandardDecoder.TryDecompress(page.Slice(PageLayout.HeaderSize, length), scratch.AsSpan(0, payload.Length), out var written)
                || written != payload.Length)
            {
                throw Damaged(expected);
            }

            // In memory the page is the same whether it was stored compressed or not.
            scratch.AsSpan(0, payload.Length).CopyTo(payload);
            page[LengthOffset..(LengthOffset + sizeof(ushort))].Clear();
            page[^PageLayout.TrailerSize..].Clear();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public void Authenticate(ReadOnlySpan<byte> data, Span<byte> check) => Inner.Authenticate(data, check);

    public void Dispose() => Inner.Dispose();

    private static InvalidDataException Damaged(PageNumber page) =>
        new($"Page {page.Value} does not decompress to a page: the file is corrupt.");
}
