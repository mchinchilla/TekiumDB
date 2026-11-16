// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

/// <summary>
/// What stands between a page in memory and its bytes on disk: a checksum, or authenticated encryption.
/// The pager is the only caller; nothing above it knows which one is in use.
/// </summary>
internal interface IPageProtector : IDisposable
{
    /// <summary>Gets a value indicating whether stored pages are encrypted.</summary>
    bool Encrypts { get; }

    /// <summary>
    /// Produces the bytes to store for a page. The spans do not overlap and <paramref name="stored"/> is at
    /// least as long as the page.
    /// </summary>
    /// <returns>How many bytes of <paramref name="stored"/> to write: the page length, or fewer if it was compressed.</returns>
    int Protect(ReadOnlySpan<byte> page, Span<byte> stored);

    /// <summary>
    /// Turns stored bytes back into the page, in place, and checks that it is the page expected.
    /// Throws <see cref="InvalidDataException"/> naming the page when it is damaged, altered or misplaced.
    /// </summary>
    void Unprotect(Span<byte> page, PageNumber expected);

    /// <summary>Computes the 16-byte check of a header record, such as a root slot.</summary>
    void Authenticate(ReadOnlySpan<byte> data, Span<byte> check);
}
