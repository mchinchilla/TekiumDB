// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Indexing;

namespace TekiumDB;

/// <summary>
/// Conditions a row must meet, all of them. Built from the fields the generator declares on the row
/// type, as in <c>Order.Fields.Total.GreaterThan(100).And(Order.Fields.Deleted.EqualTo(false))</c>.
/// </summary>
/// <typeparam name="TRow">The row type.</typeparam>
public sealed class TkFilter<TRow>
    where TRow : class, ITkRow<TRow>
{
    private readonly FilterTerm[] _terms;

    internal TkFilter(int field, KeyRange range) => _terms = [new FilterTerm(field, range)];

    private TkFilter(FilterTerm[] terms) => _terms = terms;

    /// <summary>Gets the conditions, as written: a field may appear more than once.</summary>
    internal ReadOnlySpan<FilterTerm> Terms => _terms;

    /// <summary>Requires the conditions of another filter as well.</summary>
    /// <param name="other">The other filter.</param>
    /// <returns>A filter that accepts the rows both accept.</returns>
    public TkFilter<TRow> And(TkFilter<TRow> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new TkFilter<TRow>([.. _terms, .. other._terms]);
    }

    /// <summary>One condition per field: those on the same field become the range all of them accept.</summary>
    internal FilterTerm[] Normalize()
    {
        var merged = new List<FilterTerm>(_terms.Length);
        foreach (var term in _terms)
        {
            var at = merged.FindIndex(existing => existing.Field == term.Field);
            if (at < 0)
            {
                merged.Add(term);
            }
            else
            {
                merged[at] = new FilterTerm(term.Field, merged[at].Range.Intersect(term.Range));
            }
        }

        return [.. merged];
    }
}
