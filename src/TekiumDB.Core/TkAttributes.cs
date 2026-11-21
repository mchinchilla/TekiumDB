// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB;

/// <summary>
/// Marks a partial class as a table. The source generator writes its serializer at compile time,
/// so nothing about the type is discovered by reflection at run time.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TkTableAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="TkTableAttribute"/> class, naming the table after the class.</summary>
    public TkTableAttribute()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkTableAttribute"/> class.</summary>
    /// <param name="name">Name of the table in the database. Renaming the class later does not change it.</param>
    public TkTableAttribute(string name) => Name = name;

    /// <summary>Gets the name of the table, or <see langword="null"/> to use the class name.</summary>
    public string? Name { get; }
}

/// <summary>Marks the property that identifies a row. A table has exactly one.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TkKeyAttribute : Attribute;

/// <summary>Stores a property under a name other than its own, so the property can be renamed without losing data.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TkColumnAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="TkColumnAttribute"/> class.</summary>
    /// <param name="name">Name the column is stored under.</param>
    public TkColumnAttribute(string name) => Name = name;

    /// <summary>Gets the name the column is stored under.</summary>
    public string Name { get; }
}

/// <summary>Leaves a property out of the table.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TkIgnoreAttribute : Attribute;

/// <summary>
/// Declares a secondary index. On a property, it indexes that property: a column, or a property
/// computed from columns (an index on an expression). On the class, it names the properties of an index
/// on several of them.
/// </summary>
/// <remarks>
/// A computed property must be deterministic and depend on nothing but the columns of its own row. The
/// index is rebuilt when its text changes, not when something it calls does.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class TkIndexAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="TkIndexAttribute"/> class, for the property it is on.</summary>
    public TkIndexAttribute() => Columns = [];

    /// <summary>Initializes a new instance of the <see cref="TkIndexAttribute"/> class, for several properties.</summary>
    /// <param name="columns">The names of the properties, most significant first.</param>
    public TkIndexAttribute(params string[] columns) => Columns = columns ?? [];

    /// <summary>Gets the names of the indexed properties. Empty when the attribute is on the property itself.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets or sets the name of the index. By default, the names of its properties joined by an underscore.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the name of a <see cref="bool"/> property that says which rows are in the index,
    /// which makes it a partial index. Its body must compare columns with constants, joined by
    /// <c>&amp;&amp;</c>, so that the engine can tell when a query implies it.
    /// </summary>
    public string? Where { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether two rows may not have the same values in the index: a
    /// UNIQUE constraint, named like the index. Rows with no value in any of its properties are exempt.
    /// </summary>
    public bool Unique { get; set; }
}

/// <summary>
/// Declares a CHECK constraint: the <see cref="bool"/> property it is on must be true of every row
/// written. The property must be deterministic and depend on nothing but the columns of its own row.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TkCheckAttribute : Attribute
{
    /// <summary>Gets or sets the name of the constraint. By default, the name of the property.</summary>
    public string? Name { get; set; }
}

/// <summary>
/// Indexes a value at a path of the <see cref="TkJson"/> property it is on. The index holds the rows
/// whose value there is of the kind of the index: a string (or the text of an array or object) for
/// <see cref="TkColumnType.Text"/>, a number for <see cref="TkColumnType.F64"/>. SQL declares the same
/// index with <c>CREATE INDEX … ON t (JSON_VALUE(column, 'path') [AS DOUBLE])</c>, and keeps it alike.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class TkJsonIndexAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="TkJsonIndexAttribute"/> class.</summary>
    /// <param name="path">The path, starting with <c>$</c>.</param>
    public TkJsonIndexAttribute(string path) => Path = path;

    /// <summary>Gets the path.</summary>
    public string Path { get; }

    /// <summary>Gets or sets the kind of value indexed: <see cref="TkColumnType.Text"/> (the default) or <see cref="TkColumnType.F64"/>.</summary>
    public TkColumnType Type { get; set; } = TkColumnType.Text;

    /// <summary>Gets or sets the name of the index. By default, the property and the last name of the path.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets a value indicating whether two rows may not have the same value there. Rows with none are exempt.</summary>
    public bool Unique { get; set; }
}

/// <summary>
/// Keeps a <see cref="TkJson"/> column as the text of the document, as it was written (a <c>json</c>
/// column). Without it the column keeps the binary form (<c>jsonb</c>): keys sorted and not repeated.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TkJsonTextAttribute : Attribute;

/// <summary>
/// Declares a FOREIGN KEY constraint: the column it is on holds the key of a row of another table, which
/// must exist. A nullable column may hold no value. A row cannot be deleted while others refer to it.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TkForeignKeyAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="TkForeignKeyAttribute"/> class.</summary>
    /// <param name="table">The row type of the table referred to, whose key has the type of this column.</param>
    public TkForeignKeyAttribute(Type table) => Table = table;

    /// <summary>Gets the row type of the table referred to.</summary>
    public Type Table { get; }

    /// <summary>Gets or sets the name of the constraint. By default, <c>fk_</c> and the name of the property.</summary>
    public string? Name { get; set; }
}

/// <summary>
/// Declares a zone map on a date column: for each window of time, the table remembers the range of keys
/// of the rows whose date falls in it. A query for a range of dates then reads only that stretch of the
/// table, with no index entry per row to keep.
/// </summary>
/// <remarks>
/// It pays off when rows are written roughly in date order under keys that grow, as with a time series
/// keyed by UUID v7 or by a sequence: the rows of a window are then next to each other. With keys that
/// have nothing to do with the date it is correct but saves nothing. Deleting rows does not narrow it.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TkZoneMapAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the length of each window, in minutes: one hour by default. For a <see cref="DateOnly"/>
    /// column it is rounded up to whole days.
    /// </summary>
    public int BucketMinutes { get; set; } = 60;
}
