// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;

namespace Tekium.Embedded.Sql;

/// <summary>
/// A block of rows for a vectorized function: each argument as a span of values, one per row, and the
/// span the results go into. The code the source generator writes takes the spans from here and hands
/// them to the function. A row with a NULL argument gets a zero in its place, and its result is NULL
/// whatever the function writes.
/// </summary>
public sealed class TkVectorFrame
{
    private readonly Array[] _arguments;
    private readonly Array _result;

    internal TkVectorFrame(TkRoutine routine, int rows)
    {
        Rows = rows;
        _arguments = [.. routine.VectorArguments.Select(kind => Allocate(kind, rows))];
        _result = Allocate(routine.VectorResult, rows);
        Missing = new bool[rows];
    }

    /// <summary>Gets the number of rows in the block.</summary>
    public int Rows { get; }

    /// <summary>Gets, for each row, whether one of its arguments was NULL.</summary>
    internal bool[] Missing { get; }

    /// <summary>An argument as <see cref="double"/> values.</summary>
    /// <param name="argument">Which argument, from zero.</param>
    /// <returns>One value per row.</returns>
    public ReadOnlySpan<double> Doubles(int argument) => (double[])_arguments[argument];

    /// <summary>An argument as <see cref="float"/> values.</summary>
    /// <param name="argument">Which argument, from zero.</param>
    /// <returns>One value per row.</returns>
    public ReadOnlySpan<float> Singles(int argument) => (float[])_arguments[argument];

    /// <summary>An argument as <see cref="long"/> values.</summary>
    /// <param name="argument">Which argument, from zero.</param>
    /// <returns>One value per row.</returns>
    public ReadOnlySpan<long> Int64s(int argument) => (long[])_arguments[argument];

    /// <summary>An argument as <see cref="int"/> values.</summary>
    /// <param name="argument">Which argument, from zero.</param>
    /// <returns>One value per row.</returns>
    public ReadOnlySpan<int> Int32s(int argument) => (int[])_arguments[argument];

    /// <summary>Where <see cref="double"/> results go.</summary>
    /// <returns>One place per row.</returns>
    public Span<double> DoubleResults() => (double[])_result;

    /// <summary>Where <see cref="float"/> results go.</summary>
    /// <returns>One place per row.</returns>
    public Span<float> SingleResults() => (float[])_result;

    /// <summary>Where <see cref="long"/> results go.</summary>
    /// <returns>One place per row.</returns>
    public Span<long> Int64Results() => (long[])_result;

    /// <summary>Where <see cref="int"/> results go.</summary>
    /// <returns>One place per row.</returns>
    public Span<int> Int32Results() => (int[])_result;

    /// <summary>Puts the value of an argument for a row. NULL marks the row.</summary>
    internal void Set(int argument, int row, object? value)
    {
        if (value is null)
        {
            Missing[row] = true;
            return;
        }

        switch (_arguments[argument])
        {
            case double[] doubles: doubles[row] = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
            case float[] singles: singles[row] = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case long[] longs: longs[row] = Convert.ToInt64(value, CultureInfo.InvariantCulture); break;
            default: ((int[])_arguments[argument])[row] = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
        }
    }

    /// <summary>The result of a row, as SQL has it.</summary>
    internal object? Result(int row) => Missing[row] ? null : _result switch
    {
        double[] doubles => doubles[row],
        float[] singles => singles[row],
        long[] longs => longs[row],
        _ => ((int[])_result)[row],
    };

    private static Array Allocate(TkVectorKind kind, int rows) => kind switch
    {
        TkVectorKind.F64 => new double[rows],
        TkVectorKind.F32 => new float[rows],
        TkVectorKind.I64 => new long[rows],
        _ => new int[rows],
    };
}
