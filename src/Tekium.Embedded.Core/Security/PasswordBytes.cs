// Copyright (c) Tekium. Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace Tekium.Embedded.Security;

/// <summary>
/// The password as UTF-8, held only for as long as it takes to derive the key and then wiped.
/// It exists because a span cannot cross an await; the caller's own copy is theirs to manage.
/// </summary>
internal sealed class PasswordBytes : IDisposable
{
    private readonly byte[] _bytes;

    private PasswordBytes(byte[] bytes) => _bytes = bytes;

    public ReadOnlySpan<byte> Span => _bytes;

    /// <summary>Returns <see langword="null"/> for an empty password, which means "no encryption".</summary>
    public static PasswordBytes? From(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty)
        {
            return null;
        }

        var bytes = GC.AllocateArray<byte>(Encoding.UTF8.GetByteCount(password), pinned: true);
        Encoding.UTF8.GetBytes(password, bytes);
        return new PasswordBytes(bytes);
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_bytes);
}
