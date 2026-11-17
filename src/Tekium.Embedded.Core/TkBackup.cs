// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded;

/// <summary>How <see cref="TekiumDb.BackupAsync(string, TkBackupOptions?, CancellationToken)"/> protects the copy.</summary>
public sealed record TkBackupOptions
{
    /// <summary>Gets the password of the backup. Required if the database is encrypted, unless <see cref="Unencrypted"/> says otherwise.</summary>
    public ReadOnlyMemory<char> Password { get; init; }

    /// <summary>
    /// Gets a value indicating whether the backup of an encrypted database is to be written without
    /// encryption. It has to be said: it is never the default.
    /// </summary>
    public bool Unencrypted { get; init; }
}

/// <summary>What a backup holds.</summary>
public sealed class TkBackupReport
{
    /// <summary>Gets the transaction whose commit the backup reflects: everything up to it, and nothing after.</summary>
    public ulong TransactionId { get; init; }

    /// <summary>Gets the number of entries copied: rows, index entries and whatever else the trees of the database hold.</summary>
    public long EntriesCopied { get; init; }

    /// <summary>Gets the size of the backup, in bytes: its pages. The file may be a little longer.</summary>
    public long Bytes { get; init; }
}
