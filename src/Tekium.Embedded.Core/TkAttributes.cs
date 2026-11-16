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
