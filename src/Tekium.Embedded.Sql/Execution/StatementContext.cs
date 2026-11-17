// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Sql.Execution;

/// <summary>What a statement runs with: the values of its parameters and the routines the application registered.</summary>
internal sealed class StatementContext(IReadOnlyDictionary<string, object?> values, IReadOnlyDictionary<string, TkRoutine> routines)
{
    public IReadOnlyDictionary<string, object?> Values { get; } = values;

    public IReadOnlyDictionary<string, TkRoutine> Routines { get; } = routines;

    /// <summary>Gets or sets a value indicating whether the expressions being bound are evaluated a block of rows at a time: the select list.</summary>
    public bool Batching { get; set; }

    /// <summary>Gets the results of calls to deterministic functions with constant arguments, by the text of the call: computed once per statement.</summary>
    public Dictionary<string, object?> Folded { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the calls to vectorized functions bound while <see cref="Batching"/>, innermost first.</summary>
    public List<VectorCall> VectorCalls { get; } = [];
}

/// <summary>
/// A call to a vectorized function in a query. For a block of rows it computes the arguments of all of
/// them, calls the function once, and keeps each row's result until the row asks for it.
/// </summary>
internal sealed class VectorCall(TkRoutine routine, Evaluator[] arguments)
{
    private readonly Dictionary<object?[], object?> _results = new(ReferenceEqualityComparer.Instance);

    /// <summary>Gets how many times the function was called.</summary>
    public int Calls { get; private set; }

    /// <summary>Calls the function for a block of rows.</summary>
    public void Prepare(IReadOnlyList<object?[]> rows)
    {
        _results.Clear();
        if (rows.Count == 0)
        {
            return;
        }

        var frame = new TkVectorFrame(routine, rows.Count);
        for (var row = 0; row < rows.Count; row++)
        {
            for (var argument = 0; argument < arguments.Length; argument++)
            {
                frame.Set(argument, row, arguments[argument](rows[row]));
            }
        }

        routine.Vector!(frame);
        Calls++;
        for (var row = 0; row < rows.Count; row++)
        {
            _results[rows[row]] = frame.Result(row);
        }
    }

    /// <summary>The result for a row: the one computed with its block, or computed now, by itself.</summary>
    public object? Evaluate(object?[] row)
    {
        if (_results.TryGetValue(row, out var known))
        {
            return known;
        }

        var frame = new TkVectorFrame(routine, 1);
        for (var argument = 0; argument < arguments.Length; argument++)
        {
            frame.Set(argument, 0, arguments[argument](row));
        }

        routine.Vector!(frame);
        Calls++;
        return frame.Result(0);
    }

    public void Clear() => _results.Clear();
}
