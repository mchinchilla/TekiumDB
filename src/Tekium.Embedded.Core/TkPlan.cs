// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded;

/// <summary>How a query reaches its rows.</summary>
public enum TkAccess
{
    /// <summary>Reads every row of the table and keeps those that meet the filter.</summary>
    TableScan = 0,

    /// <summary>Reads the one row with the key the filter asks for.</summary>
    KeyLookup = 1,

    /// <summary>Reads the rows whose key is in the range the filter asks for.</summary>
    KeyRange = 2,

    /// <summary>Reads a range of a secondary index, and from it the rows.</summary>
    IndexScan = 3,

    /// <summary>Reads nothing: the conditions of the filter contradict each other.</summary>
    Nothing = 4,

    /// <summary>Reads the one entry of a unique index the filter asks for: at most one row.</summary>
    UniqueLookup = 5,

    /// <summary>Reads the stretches of the table that a zone map says hold the dates the filter asks for.</summary>
    ZoneScan = 6,
}

/// <summary>The way the engine chose to run a query.</summary>
/// <param name="Access">How the rows are reached.</param>
/// <param name="Index">The secondary index used, if any.</param>
public sealed record TkPlan(TkAccess Access, string? Index);
