// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded;

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
}
