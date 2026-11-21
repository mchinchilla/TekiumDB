// Copyright (c) Tekium. Licensed under the MIT License.

using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace TekiumDB.Linq;

/// <summary>
/// A query over a table: the rows that meet its predicates, in the order the engine reads them. It runs
/// each time it is enumerated with <c>await foreach</c> or through one of its <c>…Async</c> methods.
/// </summary>
/// <remarks>
/// On purpose it is neither an <see cref="IAsyncEnumerable{T}"/> nor an <see cref="IQueryable{T}"/>: the
/// only operators it has are the ones the engine runs. Sorting, grouping or joining do not compile on
/// it, instead of quietly reading the whole table into memory. To do those in memory, say so:
/// <see cref="AsAsyncEnumerable"/>.
/// </remarks>
/// <typeparam name="T">The row type.</typeparam>
public sealed class TkQuery<T>
    where T : class, ITkRow<T>
{
    private readonly Table<T> _table;
    private readonly TkTransaction? _transaction;
    private readonly TkFilter<T>? _filter;
    private readonly int _skip;
    private readonly int? _take;

    internal TkQuery(Table<T> table, TkTransaction? transaction, TkFilter<T>? filter, int skip, int? take)
    {
        _table = table;
        _transaction = transaction;
        _filter = filter;
        _skip = skip;
        _take = take;
    }

    /// <summary>Keeps the rows that also meet another predicate.</summary>
    /// <param name="predicate">The predicate. See <see cref="TkLinq.ToFilter{T}"/> for what it may contain.</param>
    /// <returns>The narrower query.</returns>
    /// <exception cref="NotSupportedException">The engine cannot answer the predicate.</exception>
    /// <exception cref="InvalidOperationException">The query already skips or takes rows: filtering afterwards would mean something else.</exception>
    public TkQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        if (_skip != 0 || _take is not null)
        {
            throw new InvalidOperationException("Where comes before Skip and Take: the engine filters first and then cuts.");
        }

        var filter = TkLinq.ToFilter(predicate);
        return new TkQuery<T>(_table, _transaction, _filter is null ? filter : _filter.And(filter), 0, null);
    }

    /// <summary>Leaves out the first rows.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The query without them.</returns>
    public TkQuery<T> Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_take is not null)
        {
            throw new InvalidOperationException("Skip comes before Take.");
        }

        return new TkQuery<T>(_table, _transaction, _filter, _skip + count, null);
    }

    /// <summary>Stops after a number of rows. The engine reads no further.</summary>
    /// <param name="count">How many rows, at most.</param>
    /// <returns>The shorter query.</returns>
    public TkQuery<T> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return new TkQuery<T>(_table, _transaction, _filter, _skip, Math.Min(count, _take ?? int.MaxValue));
    }

    /// <summary>
    /// Gives each row another shape. The function runs in memory on the rows the engine returned: it
    /// changes what they look like, never which ones there are.
    /// </summary>
    /// <typeparam name="TResult">The type of the results.</typeparam>
    /// <param name="selector">What to make of each row.</param>
    /// <returns>The results, to enumerate.</returns>
    public TkProjection<T, TResult> Select<TResult>(Func<T, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new TkProjection<T, TResult>(this, selector);
    }

    /// <summary>Runs the query. Used by <c>await foreach</c>.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The enumerator of the rows.</returns>
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        RunAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

    /// <summary>
    /// Leaves the query for an ordinary asynchronous sequence. Whatever is done to it from here on
    /// (sorting, grouping) happens in memory, over the rows the query returns.
    /// </summary>
    /// <returns>The rows as a sequence.</returns>
    public IAsyncEnumerable<T> AsAsyncEnumerable() => RunAsync(default);

    /// <summary>Runs the query and collects its rows.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The rows.</returns>
    public async ValueTask<List<T>> ToListAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<T>();
        await foreach (var row in RunAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Runs the query until its first row.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The first row, or <see langword="null"/> if there is none.</returns>
    public async ValueTask<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        await foreach (var row in Take(1).RunAsync(cancellationToken).ConfigureAwait(false))
        {
            return row;
        }

        return null;
    }

    /// <summary>Whether the query has any row. Reads at most one.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns><see langword="true"/> if there is at least one row.</returns>
    public async ValueTask<bool> AnyAsync(CancellationToken cancellationToken = default) =>
        await FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Counts the rows of the query. It reads them all: the engine keeps no counts.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The number of rows.</returns>
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken = default)
    {
        long count = 0;
        await foreach (var unused in RunAsync(cancellationToken).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    /// <summary>Says how the engine would reach the rows of the query: which index, if any.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The plan.</returns>
    public ValueTask<TkPlan> ExplainAsync(CancellationToken cancellationToken = default) =>
        _filter is null ? new ValueTask<TkPlan>(new TkPlan(TkAccess.TableScan, null)) : _table.ExplainAsync(_filter, cancellationToken);

    private async IAsyncEnumerable<T> RunAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_take == 0)
        {
            yield break;
        }

        var rows = (_filter, _transaction) switch
        {
            (null, null) => _table.ScanAsync(cancellationToken),
            (null, { } transaction) => _table.ScanAsync(transaction, cancellationToken),
            ({ } filter, null) => _table.QueryAsync(filter, cancellationToken),
            ({ } filter, { } transaction) => _table.QueryAsync(transaction, filter, cancellationToken),
        };
        var skipped = 0;
        var taken = 0;
        await foreach (var row in rows.ConfigureAwait(false))
        {
            if (skipped < _skip)
            {
                skipped++;
                continue;
            }

            yield return row;
            if (++taken == _take)
            {
                yield break;
            }
        }
    }
}

/// <summary>The rows of a query, each given another shape by a function that runs in memory.</summary>
/// <typeparam name="T">The row type.</typeparam>
/// <typeparam name="TResult">The type of the results.</typeparam>
public sealed class TkProjection<T, TResult>
    where T : class, ITkRow<T>
{
    private readonly TkQuery<T> _query;
    private readonly Func<T, TResult> _selector;

    internal TkProjection(TkQuery<T> query, Func<T, TResult> selector)
    {
        _query = query;
        _selector = selector;
    }

    /// <summary>Runs the query. Used by <c>await foreach</c>.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The enumerator of the results.</returns>
    public IAsyncEnumerator<TResult> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        RunAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

    /// <summary>Leaves the query for an ordinary asynchronous sequence.</summary>
    /// <returns>The results as a sequence.</returns>
    public IAsyncEnumerable<TResult> AsAsyncEnumerable() => RunAsync(default);

    /// <summary>Runs the query and collects its results.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The results.</returns>
    public async ValueTask<List<TResult>> ToListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<TResult>();
        await foreach (var result in RunAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(result);
        }

        return results;
    }

    /// <summary>Runs the query until its first result.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The first result, or the default of its type if there is none.</returns>
    public async ValueTask<TResult?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        var first = await _query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return first is null ? default : _selector(first);
    }

    private async IAsyncEnumerable<TResult> RunAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var row in _query.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return _selector(row);
        }
    }
}
