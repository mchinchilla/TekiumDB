// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB;

/// <summary>What <see cref="TekiumDb.VerifyAsync(CancellationToken)"/> found in an open database.</summary>
public sealed class TkVerification
{
    /// <summary>Gets the transaction whose commit was verified: the latest when the verification started.</summary>
    public ulong TransactionId { get; init; }

    /// <summary>Gets the number of pages in use.</summary>
    public long PagesInUse { get; init; }

    /// <summary>Gets the number of pages listed as free.</summary>
    public long PagesFree { get; init; }

    /// <summary>Gets the trees found, the catalog first, with their entries, pages and depth.</summary>
    public IReadOnlyList<TkTreeSummary> Trees { get; init; } = [];

    /// <summary>Gets everything found, in the order it was found. Empty if nothing is wrong.</summary>
    public IReadOnlyList<TkFinding> Findings { get; init; } = [];

    /// <summary>Gets a value indicating whether nothing wrong was found.</summary>
    public bool IsHealthy => !Findings.Any(static finding => finding.Severity != TkSeverity.Info);
}
