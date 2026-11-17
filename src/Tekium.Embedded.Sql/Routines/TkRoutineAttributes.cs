// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Sql;

/// <summary>
/// Makes a static method a function SQL can call by name. The source generator writes, at compile time,
/// the code that passes it its arguments: there is no reflection, and it works under Native AOT.
/// </summary>
/// <remarks>
/// A scalar function takes values and returns one, once per row: <c>bool</c>, <c>int</c>, <c>long</c>,
/// <c>double</c>, <c>decimal</c>, <c>string</c> (or <c>ReadOnlySpan&lt;char&gt;</c> as a parameter),
/// <c>Guid</c>, <c>DateTime</c>, <c>DateTimeOffset</c> or <c>byte[]</c>. Given NULL for a parameter
/// that cannot be null, it is not called and the result is NULL. A function with
/// <see cref="Vectorized"/> takes whole blocks of rows instead.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class TkFunctionAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="TkFunctionAttribute"/> class.</summary>
    /// <param name="name">The name SQL calls the function by. Case does not matter.</param>
    public TkFunctionAttribute(string name) => Name = name;

    /// <summary>Gets the name SQL calls the function by.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the function always returns the same result for the same
    /// arguments and does nothing else. If so, a call with constant arguments is computed once, and
    /// within a query the result for arguments already seen is reused.
    /// </summary>
    public bool Deterministic { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the function works on blocks of rows: its parameters are
    /// <c>ReadOnlySpan&lt;T&gt;</c>, one per argument, followed by a <c>Span&lt;T&gt;</c> for the results,
    /// with <c>T</c> one of <c>double</c>, <c>float</c>, <c>long</c> or <c>int</c>; it returns nothing.
    /// It is called once for about a thousand rows instead of once per row.
    /// </summary>
    public bool Vectorized { get; set; }
}

/// <summary>
/// Makes a static method a procedure SQL runs with <c>CALL name(…)</c>, inside the transaction of the
/// statement. Its first parameter is a <see cref="TkContext"/>; the rest are its arguments; a
/// <see cref="CancellationToken"/> may come last. It returns a <c>ValueTask</c> or a <c>Task</c>, with
/// or without a value.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class TkProcedureAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="TkProcedureAttribute"/> class.</summary>
    /// <param name="name">The name SQL calls the procedure by. Case does not matter.</param>
    public TkProcedureAttribute(string name) => Name = name;

    /// <summary>Gets the name SQL calls the procedure by.</summary>
    public string Name { get; }
}
