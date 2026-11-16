// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded;

/// <summary>
/// What the engine needs to know about a row type. The source generator implements it on every class
/// marked with <see cref="TkTableAttribute"/>; it is not meant to be written by hand.
/// </summary>
/// <typeparam name="TSelf">The row type itself.</typeparam>
public interface ITkRow<TSelf>
    where TSelf : class, ITkRow<TSelf>
{
    /// <summary>Gets the name of the table in the database.</summary>
    static abstract string TableName { get; }

    /// <summary>Gets the columns, in the order <see cref="Write"/> writes them.</summary>
    static abstract ReadOnlySpan<TkColumn> Columns { get; }

    /// <summary>Gets the index in <see cref="Columns"/> of the key column.</summary>
    static abstract int KeyColumn { get; }

    /// <summary>Writes every column of <paramref name="row"/>, in order.</summary>
    /// <param name="writer">Where the row goes.</param>
    /// <param name="row">The row to store.</param>
    static abstract void Write(ref TkRowWriter writer, TSelf row);

    /// <summary>Builds a row from stored bytes, which may have been written by an older version of the type.</summary>
    /// <param name="reader">The stored row.</param>
    /// <returns>The row.</returns>
    static abstract TSelf Read(ref TkRowReader reader);

    /// <summary>Writes the key of <paramref name="row"/> so that the order of the bytes is the order of the keys.</summary>
    /// <param name="row">The row.</param>
    /// <param name="destination">Where the key goes.</param>
    /// <returns>The number of bytes written.</returns>
    static abstract int WriteKey(TSelf row, Span<byte> destination);

    /// <summary>Gets the values computed from the columns that can be indexed. Their fields follow those of the columns.</summary>
    static virtual ReadOnlySpan<TkExpression> Expressions => default;

    /// <summary>Gets the secondary indexes declared on the type.</summary>
    static virtual ReadOnlySpan<TkIndex<TSelf>> Indexes => default;

    /// <summary>Writes one field of <paramref name="row"/> so that the order of the bytes is the order of the values.</summary>
    /// <param name="writer">Where the value goes.</param>
    /// <param name="row">The row.</param>
    /// <param name="field">The field: the position of a column, or past the columns, of an expression.</param>
    static virtual void WriteField(ref TkKeyWriter writer, TSelf row, int field) =>
        throw new NotSupportedException("This row type declares no fields to index or filter by.");
}
