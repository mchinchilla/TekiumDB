// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Security;

namespace TekiumDB;

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

    /// <summary>
    /// Gets a value indicating whether a new database compresses its pages with Zstandard. An existing
    /// file keeps whatever it was created with. It reduces the bytes written and encrypted, not the size
    /// of the file: every page keeps its place.
    /// </summary>
    public bool Compression { get; init; }

    /// <summary>Gets the Zstandard level pages are compressed with: higher is smaller and slower.</summary>
    public int CompressionLevel { get; init; } = 3;

    /// <summary>
    /// Gets how many past commits stay readable, also after reopening: so that their changes can be
    /// watched with <see cref="TekiumDb.WatchAsync{T}"/>, and so that the database can be read as it was
    /// at any of them with <see cref="TekiumDb.AsOfAsync"/>. Zero, the default, keeps none: no change
    /// feed and no past. The pages of those commits are not reused meanwhile: the file is larger.
    /// </summary>
    public int ChangeRetention { get; init; }

    /// <summary>
    /// Gets a value indicating whether the database publishes counters through a
    /// <see cref="System.Diagnostics.Metrics.Meter"/> named <see cref="TekiumDb.MeterName"/>: cache hits
    /// and misses, pages written and time taken by each commit, live snapshots and the age of the
    /// oldest, and the depth of each tree. Off by default, and then nothing is counted at all.
    /// </summary>
    public bool Diagnostics { get; init; }

    /// <summary>
    /// Gets the name the counters of this database carry in their <c>tekium.db</c> tag, to tell several
    /// databases apart. By default, the path of the file, or <c>memory</c>.
    /// </summary>
    public string? DiagnosticsName { get; init; }

    /// <summary>Gets how much memory the page cache may use, in mebibytes.</summary>
    public int CacheSizeMb { get; init; } = 256;

    /// <summary>Gets the cost of deriving the key from the password of a new encrypted database.</summary>
    internal Argon2Parameters KeyDerivation { get; init; } = Argon2Parameters.Default;

    /// <summary>
    /// Gets a value indicating whether procedures stored in the file may be created and run (the SQL
    /// layer's <c>CREATE PROCEDURE</c> and <c>CALL</c>). Off by default: a stored procedure is program
    /// inside a data file, and opening someone else's file must not run any of it until the application
    /// says so.
    /// </summary>
    public bool AllowStoredProcedures { get; init; }

    /// <summary>
    /// Gets how many instructions a call to a stored procedure may execute, counting the procedures it
    /// calls, before it is stopped with an error: what keeps an endless loop from hanging the process.
    /// </summary>
    public int ProcedureInstructionLimit { get; init; } = 10_000_000;

    /// <summary>The deepest <see cref="ProcedureCallDepth"/> can be set: each level uses stack of the thread that runs it.</summary>
    public const int MaxProcedureCallDepth = 256;

    /// <summary>Gets how deep stored procedures may call one another before the call is stopped with an error. At most <see cref="MaxProcedureCallDepth"/>.</summary>
    public int ProcedureCallDepth { get; init; } = 32;
}
