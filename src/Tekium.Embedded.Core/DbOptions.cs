// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Security;

namespace Tekium.Embedded;

/// <summary>How to open a database.</summary>
public sealed class DbOptions
{
    /// <summary>Gets the path of the database file. It is created if it does not exist.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets a value indicating whether the database lives in memory and disappears when it is disposed.
    /// Everything else behaves as with a file, which is what makes it useful for tests.
    /// </summary>
    public bool InMemory { get; init; }

    /// <summary>
    /// Gets the password. Empty means no encryption. It is used to derive the key and is not kept:
    /// pass a <see cref="char"/> array to be able to wipe it afterwards.
    /// </summary>
    public ReadOnlyMemory<char> Password { get; init; }

    /// <summary>Gets the page size of a new database: 4096, 8192, 16384 or 32768. An existing file keeps its own.</summary>
    public int PageSize { get; init; } = 8192;

    /// <summary>Gets how much memory the page cache may use, in mebibytes.</summary>
    public int CacheSizeMb { get; init; } = 256;

    /// <summary>Gets the cost of deriving the key from the password of a new encrypted database.</summary>
    internal Argon2Parameters KeyDerivation { get; init; } = Argon2Parameters.Default;
}
