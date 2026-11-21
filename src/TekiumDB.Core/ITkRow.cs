// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB;

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

    /// <summary>
    /// Finds the field of a stored property, or of an indexed expression, by the name of the property.
    /// The generator implements it with the fields of the nested <c>Fields</c> class: no reflection.
    /// </summary>
    /// <param name="name">The name of the property, as written in the row type.</param>
    /// <returns>The field, or <see langword="null"/> if the row type stores no such property.</returns>
    static virtual TkField<TSelf>? FindField(string name) => null;

    /// <summary>Gets the CHECK constraints declared on the type.</summary>
    static virtual ReadOnlySpan<TkCheck> Checks => default;

    /// <summary>Gets the FOREIGN KEY constraints declared on the type.</summary>
    static virtual ReadOnlySpan<TkForeignKey> ForeignKeys => default;

    /// <summary>Evaluates one CHECK constraint on <paramref name="row"/>.</summary>
    /// <param name="row">The row.</param>
    /// <param name="check">The position of the constraint in <see cref="Checks"/>.</param>
    /// <returns>Whether the row meets it.</returns>
    static virtual bool Check(TSelf row, int check) =>
        throw new NotSupportedException("This row type declares no CHECK constraints.");

    /// <summary>Writes the key of the row that <paramref name="row"/> refers to through one FOREIGN KEY, as that table stores it.</summary>
    /// <param name="row">The row.</param>
    /// <param name="foreignKey">The position of the constraint in <see cref="ForeignKeys"/>.</param>
    /// <param name="destination">Where the key goes.</param>
    /// <returns>The number of bytes written, or -1 if the column has no value.</returns>
    static virtual int WriteForeignKey(TSelf row, int foreignKey, Span<byte> destination) =>
        throw new NotSupportedException("This row type declares no FOREIGN KEY constraints.");
}
