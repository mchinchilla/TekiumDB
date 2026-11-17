// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded;

/// <summary>How much a finding of an inspection matters.</summary>
public enum TkSeverity
{
    /// <summary>Something worth knowing that is not a problem.</summary>
    Info = 0,

    /// <summary>Something is off, but no data the database holds is affected.</summary>
    Warning = 1,

    /// <summary>Data is damaged or cannot be reached.</summary>
    Error = 2,
}

/// <summary>What an inspection found.</summary>
public enum TkFindingKind
{
    /// <summary>The file is not a TekiumDB database, or too little of it is left to tell.</summary>
    NotADatabase = 0,

    /// <summary>The file was written by a format this build does not read.</summary>
    UnsupportedFormat = 1,

    /// <summary>One of the two copies of the header is damaged.</summary>
    HeaderDamaged = 2,

    /// <summary>The file is shorter than the database says it is.</summary>
    Truncated = 3,

    /// <summary>One of the two records of where the database is does not pass its check.</summary>
    RootSlotDamaged = 4,

    /// <summary>Neither record of where the database is passes its check: nothing can be reached.</summary>
    NoValidRootSlot = 5,

    /// <summary>The database is encrypted and no password was given: its pages were not checked.</summary>
    PasswordRequired = 6,

    /// <summary>The password does not open the database, or its key data is damaged.</summary>
    WrongPassword = 7,

    /// <summary>A page fails its check, holds something else than it should, or is not where it should be.</summary>
    PageDamaged = 8,

    /// <summary>A tree is not built the way it must be: keys out of order, a page reached twice, leaves at different depths.</summary>
    TreeDamaged = 9,

    /// <summary>The list of free pages cannot be read.</summary>
    FreeListDamaged = 10,

    /// <summary>A page is listed as free and is also in use.</summary>
    FreePageInUse = 11,

    /// <summary>Pages are neither in use nor listed as free: space the database will not reuse.</summary>
    PagesUnaccounted = 12,

    /// <summary>The catalog holds an entry this build does not know.</summary>
    UnknownCatalogEntry = 13,
}

/// <summary>One thing an inspection found.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Page">The page it is about, if it is about one.</param>
/// <param name="Where">What the page belongs to, such as <c>table orders</c>, if known.</param>
/// <param name="Message">The finding in words.</param>
public sealed record TkFinding(TkSeverity Severity, TkFindingKind Kind, long? Page, string? Where, string Message);

/// <summary>One of the two records of where the database is. A commit writes the one that is not current.</summary>
/// <param name="Index">Which of the two.</param>
/// <param name="IsValid">Whether it passes its check.</param>
/// <param name="TransactionId">The transaction that wrote it.</param>
/// <param name="PageCount">How many pages the file had then.</param>
/// <param name="RootPage">The page everything hangs from.</param>
public sealed record TkRootSlot(int Index, bool IsValid, ulong TransactionId, long PageCount, long RootPage);

/// <summary>A tree of the database, as an inspection found it.</summary>
/// <param name="Name">What it is: <c>catalog</c>, <c>table orders</c>, <c>index orders.Customer</c>.</param>
/// <param name="Entries">How many entries could be read.</param>
/// <param name="Pages">How many pages it takes, counting those of large values.</param>
/// <param name="DamagedPages">How many of its pages could not be read. What hangs from them is not counted.</param>
public sealed record TkTreeSummary(string Name, long Entries, long Pages, long DamagedPages);

/// <summary>What <see cref="TkInspector"/> found in a database file, read without opening it.</summary>
public sealed class TkInspection
{
    /// <summary>Gets the length of the file, in bytes.</summary>
    public long FileLength { get; init; }

    /// <summary>Gets the format version the file says it has, or zero if it is not a database.</summary>
    public int FormatVersion { get; init; }

    /// <summary>Gets the page size, in bytes, or zero if it could not be read.</summary>
    public int PageSize { get; init; }

    /// <summary>Gets a value indicating whether the file is encrypted.</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>Gets a value indicating whether its pages are compressed.</summary>
    public bool IsCompressed { get; init; }

    /// <summary>Gets the two records of where the database is. Empty if the header could not be read or checked.</summary>
    public IReadOnlyList<TkRootSlot> RootSlots { get; init; } = [];

    /// <summary>Gets the record the database would open with, or -1 if there is none.</summary>
    public int CurrentSlot { get; init; } = -1;

    /// <summary>Gets a value indicating whether the pages were read and checked. Not without the password of an encrypted file.</summary>
    public bool PagesChecked { get; init; }

    /// <summary>Gets the number of pages in use.</summary>
    public long PagesInUse { get; init; }

    /// <summary>Gets the number of pages listed as free.</summary>
    public long PagesFree { get; init; }

    /// <summary>Gets the trees found, the catalog first.</summary>
    public IReadOnlyList<TkTreeSummary> Trees { get; init; } = [];

    /// <summary>Gets everything found, in the order it was found.</summary>
    public IReadOnlyList<TkFinding> Findings { get; init; } = [];

    /// <summary>Gets a value indicating whether nothing wrong was found, as far as the inspection could look.</summary>
    public bool IsHealthy => !Findings.Any(static finding => finding.Severity != TkSeverity.Info);
}
