// Copyright (c) Tekium. Licensed under the MIT License.

using System.Linq.Expressions;

namespace Tekium.Embedded.Linq;

/// <summary>
/// Queries written as C# predicates. A predicate is translated to a filter of the engine when the query
/// is built; one the engine cannot answer is refused then, with <see cref="NotSupportedException"/>.
/// </summary>
public static class TkLinq
{
    /// <summary>Starts a query over the rows of a table as they are when it is run.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="table">The table.</param>
    /// <returns>A query over every row.</returns>
    public static TkQuery<T> Query<T>(this Table<T> table)
        where T : class, ITkRow<T>
    {
        ArgumentNullException.ThrowIfNull(table);
        return new TkQuery<T>(table, null, null, 0, null);
    }

    /// <summary>Starts a query over the rows of a table as a transaction sees them.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="table">The table.</param>
    /// <param name="transaction">A read transaction, a write transaction (which sees its own changes) or one over a past state.</param>
    /// <returns>A query over every row.</returns>
    public static TkQuery<T> Query<T>(this Table<T> table, TkTransaction transaction)
        where T : class, ITkRow<T>
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(transaction);
        return new TkQuery<T>(table, transaction, null, 0, null);
    }

    /// <summary>Starts a query over the rows of a table that meet a predicate.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="table">The table.</param>
    /// <param name="predicate">The predicate. See <see cref="ToFilter{T}"/> for what it may contain.</param>
    /// <returns>The query.</returns>
    /// <exception cref="NotSupportedException">The engine cannot answer the predicate.</exception>
    public static TkQuery<T> Where<T>(this Table<T> table, Expression<Func<T, bool>> predicate)
        where T : class, ITkRow<T> =>
        table.Query().Where(predicate);

    /// <summary>
    /// Translates a predicate to a filter of the engine, to use with <see cref="Table{T}.QueryAsync(TkFilter{T}, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// A predicate is conditions joined by <c>&amp;&amp;</c>. Each compares a stored property (or an
    /// indexed expression) with a value, with <c>==</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> or
    /// <c>&gt;=</c>; or asks whether it is null; or is a <see langword="bool"/> property, by itself or
    /// negated. The value may be anything that does not depend on the row, and is computed once, here.
    /// There is no <c>||</c>, no <c>!=</c>, and no method called on a property.
    /// </remarks>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="predicate">The predicate.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="NotSupportedException">The engine cannot answer the predicate. The message says which part and why.</exception>
    public static TkFilter<T> ToFilter<T>(Expression<Func<T, bool>> predicate)
        where T : class, ITkRow<T> =>
        PredicateTranslator<T>.Translate(predicate);
}
