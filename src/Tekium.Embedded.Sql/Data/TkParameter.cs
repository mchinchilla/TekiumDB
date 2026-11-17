// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Tekium.Embedded.Sql;

/// <summary>A value given to a statement by name: <c>@name</c> in the text.</summary>
public sealed class TkParameter : DbParameter
{
    /// <summary>Initializes a new instance of the <see cref="TkParameter"/> class.</summary>
    public TkParameter()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TkParameter"/> class.</summary>
    /// <param name="name">The name, with or without its <c>@</c>.</param>
    /// <param name="value">The value.</param>
    public TkParameter(string name, object? value)
    {
        ParameterName = name;
        Value = value;
    }

    /// <inheritdoc/>
    public override DbType DbType { get; set; } = DbType.Object;

    /// <inheritdoc/>
    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
            {
                throw new NotSupportedException("Parameters only carry values into a statement.");
            }
        }
    }

    /// <inheritdoc/>
    public override bool IsNullable { get; set; } = true;

    /// <inheritdoc/>
    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override int Size { get; set; }

    /// <inheritdoc/>
    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override bool SourceColumnNullMapping { get; set; }

    /// <inheritdoc/>
    public override object? Value { get; set; }

    /// <inheritdoc/>
    public override void ResetDbType() => DbType = DbType.Object;
}

/// <summary>The parameters of a command.</summary>
public sealed class TkParameterCollection : DbParameterCollection, IReadOnlyList<TkParameter>
{
    private readonly List<TkParameter> _parameters = [];

    /// <inheritdoc/>
    public override int Count => _parameters.Count;

    /// <inheritdoc/>
    public override object SyncRoot => ((ICollection)_parameters).SyncRoot;

    /// <inheritdoc/>
    TkParameter IReadOnlyList<TkParameter>.this[int index] => _parameters[index];

    /// <inheritdoc/>
    IEnumerator<TkParameter> IEnumerable<TkParameter>.GetEnumerator() => _parameters.GetEnumerator();

    /// <summary>Adds a parameter by name and value.</summary>
    /// <param name="name">The name, with or without its <c>@</c>.</param>
    /// <param name="value">The value.</param>
    /// <returns>The parameter added.</returns>
    public TkParameter AddWithValue(string name, object? value)
    {
        var parameter = new TkParameter(name, value);
        _parameters.Add(parameter);
        return parameter;
    }

    /// <inheritdoc/>
    public override int Add(object value)
    {
        _parameters.Add(Cast(value));
        return _parameters.Count - 1;
    }

    /// <inheritdoc/>
    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
        {
            _parameters.Add(Cast(value));
        }
    }

    /// <inheritdoc/>
    public override void Clear() => _parameters.Clear();

    /// <inheritdoc/>
    public override bool Contains(object value) => _parameters.Contains(value as TkParameter ?? new TkParameter());

    /// <inheritdoc/>
    public override bool Contains(string value) => IndexOf(value) >= 0;

    /// <inheritdoc/>
    public override void CopyTo(Array array, int index) => ((ICollection)_parameters).CopyTo(array, index);

    /// <inheritdoc/>
    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    /// <inheritdoc/>
    public override int IndexOf(object value) => value is TkParameter parameter ? _parameters.IndexOf(parameter) : -1;

    /// <inheritdoc/>
    public override int IndexOf(string parameterName) =>
        _parameters.FindIndex(parameter => string.Equals(Bare(parameter.ParameterName), Bare(parameterName), StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public override void Insert(int index, object value) => _parameters.Insert(index, Cast(value));

    /// <inheritdoc/>
    public override void Remove(object value) => _parameters.Remove(Cast(value));

    /// <inheritdoc/>
    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    /// <inheritdoc/>
    public override void RemoveAt(string parameterName) => _parameters.RemoveAt(Existing(parameterName));

    internal Dictionary<string, object?> ToValues()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in _parameters)
        {
            values[Bare(parameter.ParameterName)] = parameter.Value;
        }

        return values;
    }

    /// <inheritdoc/>
    protected override DbParameter GetParameter(int index) => _parameters[index];

    /// <inheritdoc/>
    protected override DbParameter GetParameter(string parameterName) => _parameters[Existing(parameterName)];

    /// <inheritdoc/>
    protected override void SetParameter(int index, DbParameter value) => _parameters[index] = Cast(value);

    /// <inheritdoc/>
    protected override void SetParameter(string parameterName, DbParameter value) => _parameters[Existing(parameterName)] = Cast(value);

    private static string Bare(string name) => name.TrimStart('@', ':', '$');

    private static TkParameter Cast(object? value) =>
        value as TkParameter ?? throw new ArgumentException("A TekiumDB command takes TkParameter parameters.", nameof(value));

    private int Existing(string name) => IndexOf(name) is >= 0 and var index ? index : throw new ArgumentException($"There is no parameter '{name}'.", nameof(name));
}
