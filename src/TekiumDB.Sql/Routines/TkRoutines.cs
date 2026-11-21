// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using TekiumDB.Sql.Execution;
using TekiumDB.Sql.Storage;

namespace TekiumDB.Sql;

/// <summary>Calls a scalar function with the arguments of one row. Written by the source generator.</summary>
/// <param name="arguments">The arguments, as SQL has them.</param>
/// <returns>The result, or <see langword="null"/> for NULL.</returns>
public delegate object? TkScalarInvoker(ReadOnlySpan<object?> arguments);

/// <summary>Calls a vectorized function with the arguments of a block of rows. Written by the source generator.</summary>
/// <param name="frame">The block: its arguments, and where the results go.</param>
public delegate void TkVectorInvoker(TkVectorFrame frame);

/// <summary>Calls a procedure. Written by the source generator.</summary>
/// <param name="context">The transaction it runs in.</param>
/// <param name="arguments">Its arguments, as SQL has them.</param>
/// <param name="cancellationToken">Cancels the procedure.</param>
/// <returns>What the procedure returned, if anything.</returns>
public delegate ValueTask<object?> TkProcedureInvoker(TkContext context, object?[] arguments, CancellationToken cancellationToken);

/// <summary>The element type of the blocks a vectorized function works on.</summary>
public enum TkVectorKind
{
    /// <summary><see cref="double"/>.</summary>
    F64 = 0,

    /// <summary><see cref="float"/>.</summary>
    F32 = 1,

    /// <summary><see cref="long"/>.</summary>
    I64 = 2,

    /// <summary><see cref="int"/>.</summary>
    I32 = 3,
}

/// <summary>A function or procedure the application gives to SQL. The source generator creates them from <see cref="TkFunctionAttribute"/> and <see cref="TkProcedureAttribute"/>.</summary>
public sealed class TkRoutine
{
    private TkRoutine(string name, int parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        ParameterCount = parameters;
    }

    /// <summary>Gets the name SQL calls it by.</summary>
    public string Name { get; }

    /// <summary>Gets how many arguments it takes.</summary>
    public int ParameterCount { get; }

    /// <summary>Gets a value indicating whether the same arguments always give the same result.</summary>
    public bool IsDeterministic { get; private init; }

    internal TkScalarInvoker? Scalar { get; private init; }

    internal TkVectorInvoker? Vector { get; private init; }

    internal TkVectorKind[] VectorArguments { get; private init; } = [];

    internal TkVectorKind VectorResult { get; private init; }

    internal TkProcedureInvoker? Procedure { get; private init; }

    /// <summary>A scalar function.</summary>
    /// <param name="name">The name SQL calls it by.</param>
    /// <param name="parameters">How many arguments it takes.</param>
    /// <param name="deterministic">Whether the same arguments always give the same result.</param>
    /// <param name="invoker">What calls it.</param>
    /// <returns>The routine.</returns>
    public static TkRoutine Function(string name, int parameters, bool deterministic, TkScalarInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        return new TkRoutine(name, parameters) { IsDeterministic = deterministic, Scalar = invoker };
    }

    /// <summary>A vectorized function.</summary>
    /// <param name="name">The name SQL calls it by.</param>
    /// <param name="arguments">The element type of each argument.</param>
    /// <param name="result">The element type of the results.</param>
    /// <param name="deterministic">Whether the same arguments always give the same result.</param>
    /// <param name="invoker">What calls it.</param>
    /// <returns>The routine.</returns>
    public static TkRoutine VectorFunction(string name, TkVectorKind[] arguments, TkVectorKind result, bool deterministic, TkVectorInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(invoker);
        return new TkRoutine(name, arguments.Length) { IsDeterministic = deterministic, Vector = invoker, VectorArguments = arguments, VectorResult = result };
    }

    /// <summary>A procedure.</summary>
    /// <param name="name">The name SQL calls it by.</param>
    /// <param name="parameters">How many arguments it takes, not counting its context.</param>
    /// <param name="invoker">What calls it.</param>
    /// <returns>The routine.</returns>
    public static TkRoutine StoredProcedure(string name, int parameters, TkProcedureInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        return new TkRoutine(name, parameters) { Procedure = invoker };
    }
}

/// <summary>What a procedure runs in: the database and the write transaction of the <c>CALL</c>.</summary>
public sealed class TkContext
{
    internal TkContext(TekiumDb database, TkWriteTransaction transaction)
    {
        Database = database;
        Transaction = transaction;
    }

    /// <summary>Gets the database.</summary>
    public TekiumDb Database { get; }

    /// <summary>Gets the transaction the procedure is part of: what it writes commits, or not, with the statement that called it.</summary>
    public TkWriteTransaction Transaction { get; }

    /// <summary>Runs a SQL statement inside the transaction of the procedure.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">The values of its parameters, by name, if it has any.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The rows an INSERT, UPDATE or DELETE changed; -1 for anything else.</returns>
    public async ValueTask<int> ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) =>
        (await RunAsync(sql, parameters, cancellationToken).ConfigureAwait(false)).RecordsAffected;

    /// <summary>Runs a query inside the transaction of the procedure: it sees what the procedure has written.</summary>
    /// <param name="sql">The query.</param>
    /// <param name="parameters">The values of its parameters, by name, if it has any.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The rows, each with the values of its columns.</returns>
    public async ValueTask<IReadOnlyList<object?[]>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) =>
        (await RunAsync(sql, parameters, cancellationToken).ConfigureAwait(false)).Rows;

    private ValueTask<SqlResult> RunAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sql);
        var engine = SqlEngine.For(Database);
        return SqlEngine.ExecuteAsync(
            Transaction, engine.Parse(sql), new StatementContext(parameters ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), engine.Routines), cancellationToken);
    }
}

/// <summary>
/// Turns an argument, as SQL has it, into the type a function declares. Used by the code the source
/// generator writes; a value that cannot be converted throws <see cref="TkSqlException"/>.
/// </summary>
public static class TkArgument
{
    /// <summary>The argument as a <see cref="bool"/>.</summary>
    /// <param name="value">The argument. Not null.</param>
    /// <returns>The value.</returns>
    public static bool ToBoolean(object value) => (bool)Convert(value, TkColumnType.Boolean);

    /// <summary>The argument as an <see cref="int"/>.</summary>
    /// <param name="value">The argument. Not null.</param>
    /// <returns>The value.</returns>
    public static int ToInt32(object value) => (int)Convert(value, TkColumnType.I32);

    /// <summary>The argument as a <see cref="long"/>.</summary>
    /// <param name="value">The argument. Not null.</param>
    /// <returns>The value.</returns>
    public static long ToInt64(object value) => (long)Convert(value, TkColumnType.I64);

    /// <summary>The argument as a <see cref="double"/>.</summary>
    /// <param name="value">The argument. Not null.</param>
    /// <returns>The value.</returns>
    public static double ToDouble(object value) => (double)Convert(value, TkColumnType.F64);

    /// <summary>The argument as a <see cref="decimal"/>.</summary>
    /// <param name="value">The argument. Not null.</param>
    /// <returns>The value.</returns>
    public static decimal ToDecimal(object value) => (decimal)Convert(value, TkColumnType.Numeric);

    /// <summary>The argument as text.</summary>
    /// <param name="value">The argument. Not null.</param>
    /// <returns>The value.</returns>
    public static string ToText(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>The argument as a <see cref="Guid"/>.</summary>
    /// <param name="value">The argument. Not null.</param>
    /// <returns>The value.</returns>
    public static Guid ToGuid(object value) => (Guid)Convert(value, TkColumnType.Uuid);

    /// <summary>The argument as a <see cref="DateTime"/>.</summary>
    /// <param name="value">The argument. Not null.</param>
    /// <returns>The value.</returns>
    public static DateTime ToDateTime(object value) => (DateTime)Convert(value, TkColumnType.DateTime);

    /// <summary>The argument as a <see cref="DateTimeOffset"/>.</summary>
    /// <param name="value">The argument. Not null.</param>
    /// <returns>The value.</returns>
    public static DateTimeOffset ToDateTimeOffset(object value) => (DateTimeOffset)Convert(value, TkColumnType.DateTimeOffset);

    /// <summary>The argument as bytes.</summary>
    /// <param name="value">The argument. Not null.</param>
    /// <returns>The value.</returns>
    public static byte[] ToBytes(object value) => (byte[])Convert(value, TkColumnType.Bytes);

    private static object Convert(object value, TkColumnType type)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SqlValues.Convert(value, type, "the argument of a function");
    }
}
